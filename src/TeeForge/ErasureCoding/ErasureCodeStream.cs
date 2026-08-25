using System.Buffers;
using System.Diagnostics;
using System.IO.Hashing;
using TeeForge.ErasureCoding.Internal;
using TeeForge.RandomAccess;

namespace TeeForge.ErasureCoding;

/// <summary>Provides a fixed-capacity seekable stream striped across data and Reed-Solomon parity members.</summary>
public class ErasureCodeStream : Stream, ITeeRandomAccessStream
{
    private readonly Stream[] _suppliedStreams;
    private readonly ErasureCodeStreamOptions _options;
    private readonly ErasureSetMetadata _set;
    private readonly ReedSolomonCodec _codec;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _maintenanceGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly object _callbackLock = new();
    private readonly Dictionary<long, Action<ErasureCodeStreamStateChangedEventArgs>> _stateHandlers = [];
    private readonly Dictionary<long, Action<ErasureMaintenanceProgress>> _maintenanceHandlers = [];
    private ErasureCodeStreamState _lastState;
    private long _nextCallbackId;
    private long _position;
    private Exception? _fault;
    private bool _disposed;

    private ErasureCodeStream(
        Stream[] suppliedStreams,
        ErasureCodeStreamOptions options,
        ErasureSetMetadata set)
    {
        _suppliedStreams = suppliedStreams;
        _options = options;
        _set = set;
        _codec = new ReedSolomonCodec(set.Configuration.DataShardCount, set.Configuration.ParityShardCount);
        _lastState = CaptureState();
        foreach (ErasureMemberDevice? member in set.Members)
        {
            member?.SetConditionChangedHandler(PublishStateIfChanged);
        }
    }

    /// <summary>Formats empty member streams and creates an erasure-coded stream.</summary>
    public static ErasureCodeStream Create(
        IEnumerable<Stream> members,
        int dataShardCount,
        int parityShardCount,
        long logicalCapacity,
        int shardSize = ErasureFormatV1.DefaultShardSize,
        ErasureCodeStreamOptions? options = null) =>
        CreateAsync(
            members,
            dataShardCount,
            parityShardCount,
            logicalCapacity,
            shardSize,
            options).AsTask().GetAwaiter().GetResult();

    /// <summary>Formats empty member streams and creates an erasure-coded stream.</summary>
    public static async ValueTask<ErasureCodeStream> CreateAsync(
        IEnumerable<Stream> members,
        int dataShardCount,
        int parityShardCount,
        long logicalCapacity,
        int shardSize = ErasureFormatV1.DefaultShardSize,
        ErasureCodeStreamOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Stream[] streams = MaterializeMembers(members);
        options ??= ErasureCodeStreamOptions.Default;
        if (options.ReadOnly)
        {
            throw new ArgumentException("A newly formatted erasure set cannot be created read-only.", nameof(options));
        }

        ErasureSetMetadata set = await ErasureSetFactory.CreateAsync(
            streams,
            dataShardCount,
            parityShardCount,
            logicalCapacity,
            shardSize,
            options.JournalSlotCount,
            options.LatencySampleRate,
            cancellationToken).ConfigureAwait(false);
        return new ErasureCodeStream(streams, options, set);
    }

    /// <summary>Opens and recovers an existing erasure-coded stream from members supplied in any order.</summary>
    public static ErasureCodeStream Open(
        IEnumerable<Stream> members,
        ErasureCodeStreamOptions? options = null) =>
        OpenAsync(members, options).AsTask().GetAwaiter().GetResult();

    /// <summary>Opens and recovers an existing erasure-coded stream from members supplied in any order.</summary>
    public static async ValueTask<ErasureCodeStream> OpenAsync(
        IEnumerable<Stream> members,
        ErasureCodeStreamOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Stream[] streams = MaterializeMembers(members);
        options ??= ErasureCodeStreamOptions.Default;
        ErasureSetMetadata set;
        try
        {
            set = await ErasureSetFactory.OpenAsync(
                streams,
                options.LatencySampleRate,
                cancellationToken).ConfigureAwait(false);
            ErasureJournalSlotScanResult scan = await ErasureJournalSlotScanner.ScanAsync(
                set,
                cancellationToken).ConfigureAwait(false);
            if (!scan.Transactions.IsSuccess)
            {
                throw new InvalidDataException($"Journal scan failed: {scan.Transactions.State}.");
            }

            ErasureJournalTransaction[] requiresReplay = scan.Transactions.Transactions
                .Where(transaction => transaction.CheckpointedFragmentCount < set.WriteQuorum)
                .ToArray();
            if (requiresReplay.Length != 0)
            {
                if (options.ReadOnly)
                {
                    throw new InvalidDataException("Committed journal recovery requires writable member streams.");
                }

                var codec = new ReedSolomonCodec(
                    set.Configuration.DataShardCount,
                    set.Configuration.ParityShardCount);
                await ErasureJournalReplayExecutor.ReplayAsync(
                    set,
                    requiresReplay,
                    codec,
                    CancellationToken.None).ConfigureAwait(false);
            }

            if (scan.MaximumObservedSequence == ulong.MaxValue)
            {
                throw new InvalidDataException("The journal transaction sequence is exhausted.");
            }

            set.NextTransactionSequence = scan.MaximumObservedSequence + 1;
        }
        catch (InvalidDataException exception)
        {
            throw new ErasureCodeStreamCorruptionException("The erasure set could not be opened safely.", exception);
        }

        return new ErasureCodeStream(streams, options, set);
    }

    /// <summary>Gets the persistent erasure-set identifier.</summary>
    public Guid SetId => _set.Configuration.SetId;

    /// <summary>Gets the active stable configuration identifier.</summary>
    public Guid ConfigurationId => _set.Configuration.ConfigurationId;

    /// <summary>Gets the number of systematic data members.</summary>
    public int DataShardCount => _set.Configuration.DataShardCount;

    /// <summary>Gets the number of parity members.</summary>
    public int ParityShardCount => _set.Configuration.ParityShardCount;

    /// <summary>Gets the payload bytes stored by one member per stripe.</summary>
    public int ShardSize => checked((int)_set.Configuration.ShardSize);

    /// <summary>Gets whether options force read-only operation.</summary>
    public bool IsReadOnly => _options.ReadOnly;

    /// <summary>Captures current availability, member condition, and cumulative performance.</summary>
    public ErasureCodeStreamState GetState() => CaptureState();

    /// <summary>Registers a function that is queued when aggregate or member health changes.</summary>
    /// <param name="handler">The notification function. Exceptions thrown by it are isolated from stream I/O.</param>
    /// <param name="invokeImmediately">Whether to queue an initial notification containing the current state.</param>
    /// <returns>A registration that removes the function when disposed.</returns>
    public IDisposable RegisterStateChangeHandler(
        Action<ErasureCodeStreamStateChangedEventArgs> handler,
        bool invokeImmediately = true)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ThrowIfDisposed();
        long id = Interlocked.Increment(ref _nextCallbackId);
        ErasureCodeStreamState current;
        lock (_callbackLock)
        {
            _stateHandlers.Add(id, handler);
            current = CaptureState();
        }

        if (invokeImmediately)
        {
            QueueCallback(handler, new ErasureCodeStreamStateChangedEventArgs(current, current));
        }

        return new CallbackRegistration(() =>
        {
            lock (_callbackLock)
            {
                _stateHandlers.Remove(id);
            }
        });
    }

    /// <summary>Registers a function that receives queued maintenance lifecycle and progress notifications.</summary>
    /// <param name="handler">The notification function. Exceptions thrown by it are isolated from maintenance.</param>
    /// <returns>A registration that removes the function when disposed.</returns>
    public IDisposable RegisterMaintenanceHandler(Action<ErasureMaintenanceProgress> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ThrowIfDisposed();
        long id = Interlocked.Increment(ref _nextCallbackId);
        lock (_callbackLock)
        {
            _maintenanceHandlers.Add(id, handler);
        }

        return new CallbackRegistration(() =>
        {
            lock (_callbackLock)
            {
                _maintenanceHandlers.Remove(id);
            }
        });
    }

    /// <summary>Validates all current shard headers and integrity blocks without changing stored data.</summary>
    public async ValueTask<ErasureConsistencyCheckResult> CheckConsistencyAsync(
        ErasureMaintenanceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        options ??= ErasureMaintenanceOptions.Default;
        using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        cancellationToken = linkedCancellation.Token;
        Guid operationId = Guid.NewGuid();
        long stripeCount = checked((long)_set.Configuration.StripeCount);
        long totalBytes = checked(stripeCount * _set.MemberCount * ShardSize);
        long completedBytes = 0;
        var inconsistent = new HashSet<int>();
        long started = Stopwatch.GetTimestamp();
        await _maintenanceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        PublishMaintenance(new ErasureMaintenanceProgress(
            operationId,
            ErasureMaintenanceOperation.ConsistencyCheck,
            ErasureMaintenanceStatus.Running,
            0,
            totalBytes,
            0,
            null));

        try
        {
            for (long stripe = 0; stripe < stripeCount; stripe++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    ErasureStripeGeneration? current = await ErasureStripeMetadataReader.ReadCurrentAsync(
                        _set,
                        checked((ulong)stripe),
                        cancellationToken).ConfigureAwait(false);
                    if (current is null)
                    {
                        throw new IOException($"Stripe {stripe} does not have a uniquely decodable current generation.");
                    }

                    for (int position = 0; position < _set.MemberCount; position++)
                    {
                        ErasureStripeMemberMetadata? metadata = current.Members[position];
                        if (metadata is null)
                        {
                            inconsistent.Add(position);
                            ErasureMemberDevice? staleMember = _set.Members[position];
                            if (staleMember is not null && staleMember.Condition == ErasureMemberDeviceCondition.Online)
                            {
                                staleMember.Condition = ErasureMemberDeviceCondition.Stale;
                            }

                            completedBytes += ShardSize;
                            continue;
                        }

                        for (uint blockOffset = 0;
                             blockOffset < _set.Configuration.ShardSize;
                             blockOffset += ErasureFormatV1.IntegrityBlockSize)
                        {
                            byte[]? block = await ErasureStripeMetadataReader.ReadValidatedBlockAsync(
                                _set,
                                position,
                                checked((ulong)stripe),
                                metadata,
                                blockOffset,
                                cancellationToken).ConfigureAwait(false);
                            if (block is null)
                            {
                                inconsistent.Add(position);
                            }

                            completedBytes += ErasureFormatV1.IntegrityBlockSize;
                        }
                    }
                }
                finally
                {
                    _operationGate.Release();
                }

                PublishMaintenance(new ErasureMaintenanceProgress(
                    operationId,
                    ErasureMaintenanceOperation.ConsistencyCheck,
                    ErasureMaintenanceStatus.Running,
                    completedBytes,
                    totalBytes,
                    inconsistent.Count,
                    null));
                await ApplyMaintenancePacingAsync(options, completedBytes, started, cancellationToken).ConfigureAwait(false);
            }

            int[] positions = inconsistent.Order().ToArray();
            var result = new ErasureConsistencyCheckResult(
                operationId,
                completedBytes,
                stripeCount,
                Array.AsReadOnly(positions));
            PublishMaintenance(new ErasureMaintenanceProgress(
                operationId,
                ErasureMaintenanceOperation.ConsistencyCheck,
                ErasureMaintenanceStatus.Completed,
                completedBytes,
                totalBytes,
                inconsistent.Count,
                null));
            return result;
        }
        catch (OperationCanceledException)
        {
            PublishMaintenance(new ErasureMaintenanceProgress(
                operationId,
                ErasureMaintenanceOperation.ConsistencyCheck,
                ErasureMaintenanceStatus.Canceled,
                completedBytes,
                totalBytes,
                inconsistent.Count,
                null));
            throw;
        }
        catch (Exception exception)
        {
            PublishMaintenance(new ErasureMaintenanceProgress(
                operationId,
                ErasureMaintenanceOperation.ConsistencyCheck,
                ErasureMaintenanceStatus.Faulted,
                completedBytes,
                totalBytes,
                inconsistent.Count,
                exception));
            throw;
        }
        finally
        {
            _maintenanceGate.Release();
            PublishStateIfChanged();
        }
    }

    /// <inheritdoc />
    public override bool CanRead => !_disposed && _fault is null && ReadableMemberCount >= _set.ReadQuorum;

    /// <inheritdoc />
    public override bool CanSeek => !_disposed;

    /// <inheritdoc />
    public override bool CanWrite => !_disposed && !_options.ReadOnly && _fault is null && WritableMemberCount >= _set.WriteQuorum;

    /// <inheritdoc />
    public bool CanReadAt => CanRead;

    /// <inheritdoc />
    public bool CanWriteAt => CanWrite;

    /// <inheritdoc />
    public override long Length
    {
        get
        {
            ThrowIfUnavailable();
            return checked((long)_set.Configuration.LogicalCapacity);
        }
    }

    /// <inheritdoc />
    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return _position;
        }
        set
        {
            ThrowIfDisposed();
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, checked((long)_set.Configuration.LogicalCapacity));
            _operationGate.Wait();
            try
            {
                _position = value;
            }
            finally
            {
                _operationGate.Release();
            }
        }
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateArrayRange(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            int read = ReadAsync(rented.AsMemory(0, buffer.Length)).AsTask().GetAwaiter().GetResult();
            rented.AsSpan(0, read).CopyTo(buffer);
            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int read = await ReadAtCoreAsync(buffer, _position, cancellationToken).ConfigureAwait(false);
            _position += read;
            return read;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ValidateArrayRange(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public int ReadAt(Span<byte> buffer, long offset)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            int read = ReadAtAsync(rented.AsMemory(0, buffer.Length), offset).AsTask().GetAwaiter().GetResult();
            rented.AsSpan(0, read).CopyTo(buffer);
            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <inheritdoc />
    public async ValueTask<int> ReadAtAsync(
        Memory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        ValidateOffset(offset, allowEnd: true);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAtCoreAsync(buffer, offset, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateArrayRange(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            buffer.CopyTo(rented);
            WriteAsync(rented.AsMemory(0, buffer.Length)).AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfCannotWrite();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateWriteRange(_position, buffer.Length);
            await WriteAtCoreAsync(buffer, _position, cancellationToken).ConfigureAwait(false);
            _position += buffer.Length;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ValidateArrayRange(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public void WriteAt(ReadOnlySpan<byte> buffer, long offset)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            buffer.CopyTo(rented);
            WriteAtAsync(rented.AsMemory(0, buffer.Length), offset).AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <inheritdoc />
    public async ValueTask WriteAtAsync(
        ReadOnlyMemory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default)
    {
        ThrowIfCannotWrite();
        ValidateWriteRange(offset, buffer.Length);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAtCoreAsync(buffer, offset, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public override void Flush() => FlushAsync().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_set.Members.Select(FlushMemberAsync)).ConfigureAwait(false);
            if (WritableMemberCount < _set.WriteQuorum)
            {
                throw new IOException("The erasure stream lost write quorum while flushing members.");
            }
        }
        finally
        {
            _operationGate.Release();
        }

        async Task FlushMemberAsync(ErasureMemberDevice? member)
        {
            if (member?.CanWrite != true)
            {
                return;
            }

            try
            {
                await member.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsMemberIoFailure(exception))
            {
                member.Condition = ErasureMemberDeviceCondition.Missing;
            }
        }
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        _operationGate.Wait();
        try
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked((long)_set.Configuration.LogicalCapacity + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (target < 0 || target > (long)_set.Configuration.LogicalCapacity)
            {
                throw new IOException("Seeking outside the fixed logical capacity is not supported.");
            }

            _position = target;
            return target;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public override void SetLength(long value) =>
        throw new NotSupportedException("ErasureCodeStream has a fixed configuration capacity.");

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!disposing || _disposed)
        {
            base.Dispose(disposing);
            return;
        }

        _disposeCancellation.Cancel();
        _maintenanceGate.Wait();
        _operationGate.Wait();
        try
        {
            _disposed = true;
            PublishStateIfChanged();
            foreach (ErasureMemberDevice? member in _set.Members)
            {
                member?.SetConditionChangedHandler(null);
                member?.Dispose();
            }

            if (!_options.LeaveOpen)
            {
                List<Exception>? failures = null;
                foreach (Stream stream in _suppliedStreams)
                {
                    try
                    {
                        stream.Dispose();
                    }
                    catch (Exception exception)
                    {
                        (failures ??= []).Add(exception);
                    }
                }

                if (failures is not null)
                {
                    throw new AggregateException(failures);
                }
            }
        }
        finally
        {
            _operationGate.Release();
            _maintenanceGate.Release();
            _operationGate.Dispose();
            _maintenanceGate.Dispose();
            _disposeCancellation.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            await base.DisposeAsync().ConfigureAwait(false);
            return;
        }

        _disposeCancellation.Cancel();
        await _maintenanceGate.WaitAsync().ConfigureAwait(false);
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            PublishStateIfChanged();
            foreach (ErasureMemberDevice? member in _set.Members)
            {
                member?.SetConditionChangedHandler(null);
                member?.Dispose();
            }

            if (!_options.LeaveOpen)
            {
                List<Exception>? failures = null;
                foreach (Stream stream in _suppliedStreams)
                {
                    try
                    {
                        await stream.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        (failures ??= []).Add(exception);
                    }
                }

                if (failures is not null)
                {
                    throw new AggregateException(failures);
                }
            }
        }
        finally
        {
            _operationGate.Release();
            _maintenanceGate.Release();
            _operationGate.Dispose();
            _maintenanceGate.Dispose();
            _disposeCancellation.Dispose();
            GC.SuppressFinalize(this);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private int ReadableMemberCount => _set.Members.Count(static member => member?.CanRead == true);

    private int WritableMemberCount => _set.Members.Count(static member => member?.CanWrite == true);

    private ErasureCodeStreamState CaptureState()
    {
        var members = new ErasureMemberState[_set.MemberCount];
        bool allOnline = true;
        for (int position = 0; position < members.Length; position++)
        {
            ErasureMemberDevice? member = _set.Members[position];
            ErasureMemberPerformanceSnapshot performance = member?.GetPerformanceSnapshot() ?? default;
            ErasureMemberStatus status = member is null
                ? ErasureMemberStatus.Missing
                : MapMemberStatus(member.Condition);
            allOnline &= status == ErasureMemberStatus.Online;
            members[position] = new ErasureMemberState(
                _set.Descriptors[position].MemberId,
                position,
                position < DataShardCount ? ErasureMemberRole.Data : ErasureMemberRole.Parity,
                status,
                member?.CanRead == true,
                member?.CanWrite == true,
                new ErasureMemberPerformance(
                    performance.BytesRead,
                    performance.BytesWritten,
                    performance.ReadOperations,
                    performance.WriteOperations,
                    performance.FlushOperations,
                    performance.ReconstructionBytes,
                    performance.Errors,
                    performance.SampledReads,
                    performance.SampledWrites,
                    performance.SampledFlushes,
                    performance.ReadLatencyMilliseconds,
                    performance.WriteLatencyMilliseconds,
                    performance.FlushLatencyMilliseconds,
                    performance.ReadThroughputBytesPerSecond,
                    performance.WriteThroughputBytesPerSecond,
                    performance.MaximumSampledLatencyMilliseconds,
                    performance.LatencyBuckets ?? new long[16]));
        }

        bool canRead = !_disposed && _fault is null && ReadableMemberCount >= _set.ReadQuorum;
        bool canWrite = !_disposed && !_options.ReadOnly && _fault is null && WritableMemberCount >= _set.WriteQuorum;
        ErasureCodeStreamStatus aggregate = _disposed
            ? ErasureCodeStreamStatus.Disposed
            : _fault is not null
                ? ErasureCodeStreamStatus.Faulted
                : !canRead
                    ? ErasureCodeStreamStatus.Unavailable
                    : allOnline
                        ? ErasureCodeStreamStatus.Healthy
                        : ErasureCodeStreamStatus.Degraded;
        return new ErasureCodeStreamState(
            DateTimeOffset.UtcNow,
            aggregate,
            _options.ReadOnly,
            canRead,
            canWrite,
            _set.ReadQuorum,
            _set.WriteQuorum,
            members);
    }

    private void PublishStateIfChanged()
    {
        ErasureCodeStreamState current = CaptureState();
        ErasureCodeStreamState previous;
        Action<ErasureCodeStreamStateChangedEventArgs>[] handlers;
        lock (_callbackLock)
        {
            previous = _lastState;
            if (HasEquivalentHealth(previous, current))
            {
                return;
            }

            _lastState = current;
            handlers = _stateHandlers.Values.ToArray();
        }

        var args = new ErasureCodeStreamStateChangedEventArgs(previous, current);
        foreach (Action<ErasureCodeStreamStateChangedEventArgs> handler in handlers)
        {
            QueueCallback(handler, args);
        }
    }

    private void PublishMaintenance(ErasureMaintenanceProgress progress)
    {
        Action<ErasureMaintenanceProgress>[] handlers;
        lock (_callbackLock)
        {
            handlers = _maintenanceHandlers.Values.ToArray();
        }

        foreach (Action<ErasureMaintenanceProgress> handler in handlers)
        {
            QueueCallback(handler, progress);
        }
    }

    private static async ValueTask ApplyMaintenancePacingAsync(
        ErasureMaintenanceOptions options,
        long completedBytes,
        long started,
        CancellationToken cancellationToken)
    {
        if (options.MaximumBytesPerSecond != 0)
        {
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
            TimeSpan target = TimeSpan.FromSeconds((double)completedBytes / options.MaximumBytesPerSecond);
            if (target > elapsed)
            {
                await Task.Delay(target - elapsed, cancellationToken).ConfigureAwait(false);
            }
        }

        if (options.Priority == ErasureMaintenancePriority.Background && options.BackgroundDelay > TimeSpan.Zero)
        {
            await Task.Delay(options.BackgroundDelay, cancellationToken).ConfigureAwait(false);
        }
        else if (options.Priority == ErasureMaintenancePriority.Balanced)
        {
            await Task.Yield();
        }
    }

    private static ErasureMemberStatus MapMemberStatus(ErasureMemberDeviceCondition condition) =>
        condition switch
        {
            ErasureMemberDeviceCondition.Online => ErasureMemberStatus.Online,
            ErasureMemberDeviceCondition.Missing => ErasureMemberStatus.Missing,
            ErasureMemberDeviceCondition.Stale => ErasureMemberStatus.Stale,
            ErasureMemberDeviceCondition.Corrupt => ErasureMemberStatus.Corrupt,
            ErasureMemberDeviceCondition.Rebuilding => ErasureMemberStatus.Rebuilding,
            ErasureMemberDeviceCondition.Retired => ErasureMemberStatus.Retired,
            _ => throw new ArgumentOutOfRangeException(nameof(condition)),
        };

    private static bool HasEquivalentHealth(
        ErasureCodeStreamState left,
        ErasureCodeStreamState right)
    {
        if (left.Status != right.Status ||
            left.CanRead != right.CanRead ||
            left.CanWrite != right.CanWrite ||
            left.Members.Count != right.Members.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Members.Count; index++)
        {
            ErasureMemberState leftMember = left.Members[index];
            ErasureMemberState rightMember = right.Members[index];
            if (leftMember.Status != rightMember.Status ||
                leftMember.CanRead != rightMember.CanRead ||
                leftMember.CanWrite != rightMember.CanWrite)
            {
                return false;
            }
        }

        return true;
    }

    private static void QueueCallback<T>(Action<T> handler, T value) where T : class
    {
        ThreadPool.QueueUserWorkItem(
            static state =>
            {
                try
                {
                    state.Handler(state.Value);
                }
                catch
                {
                    // Observers cannot compromise storage I/O or maintenance.
                }
            },
            (Handler: handler, Value: value),
            preferLocal: false);
    }

    private async ValueTask<int> ReadAtCoreAsync(
        Memory<byte> destination,
        long logicalOffset,
        CancellationToken cancellationToken)
    {
        long capacity = checked((long)_set.Configuration.LogicalCapacity);
        if (destination.IsEmpty || logicalOffset == capacity)
        {
            return 0;
        }

        int total = (int)Math.Min(destination.Length, capacity - logicalOffset);
        int completed = 0;
        while (completed < total)
        {
            LogicalBlockAddress address = MapLogicalOffset(logicalOffset + completed);
            int count = Math.Min(total - completed, ErasureFormatV1.IntegrityBlockSize - address.OffsetWithinBlock);
            byte[] block = await ReadDataBlockAsync(
                address.StripeIndex,
                address.DataMemberPosition,
                address.BlockOffset,
                cancellationToken).ConfigureAwait(false);
            block.AsMemory(address.OffsetWithinBlock, count).CopyTo(destination[completed..]);
            completed += count;
        }

        return total;
    }

    private async ValueTask<byte[]> ReadDataBlockAsync(
        ulong stripeIndex,
        int dataMemberPosition,
        uint blockOffset,
        CancellationToken cancellationToken)
    {
        ErasureStripeGeneration? generation = await ErasureStripeMetadataReader.ReadCurrentAsync(
            _set,
            stripeIndex,
            cancellationToken).ConfigureAwait(false);
        if (generation is null)
        {
            return Fault<byte[]>("No stripe generation has read quorum.");
        }

        if (generation.TransactionSequence == 0)
        {
            return new byte[ErasureFormatV1.IntegrityBlockSize];
        }

        ErasureStripeMemberMetadata? directMetadata = generation.Members[dataMemberPosition];
        if (directMetadata is not null)
        {
            byte[]? direct = await ErasureStripeMetadataReader.ReadValidatedBlockAsync(
                _set,
                dataMemberPosition,
                stripeIndex,
                directMetadata,
                blockOffset,
                cancellationToken).ConfigureAwait(false);
            if (direct is not null)
            {
                return direct;
            }
        }

        var tasks = new Task<byte[]?>[_set.MemberCount];
        for (int member = 0; member < tasks.Length; member++)
        {
            int captured = member;
            ErasureStripeMemberMetadata? metadata = generation.Members[member];
            tasks[member] = metadata is null
                ? Task.FromResult<byte[]?>(null)
                : ErasureStripeMetadataReader.ReadValidatedBlockAsync(
                    _set,
                    captured,
                    stripeIndex,
                    metadata,
                    blockOffset,
                    cancellationToken).AsTask();
        }

        byte[]?[] blocks = await Task.WhenAll(tasks).ConfigureAwait(false);
        var shards = new byte[_set.MemberCount][];
        var present = new bool[_set.MemberCount];
        for (int member = 0; member < shards.Length; member++)
        {
            shards[member] = blocks[member] ?? new byte[ErasureFormatV1.IntegrityBlockSize];
            present[member] = blocks[member] is not null;
        }

        if (present.Count(static value => value) < _set.ReadQuorum)
        {
            return Fault<byte[]>("Fewer than k valid blocks remain in the selected stripe generation.");
        }

        _codec.Reconstruct(shards, present, 0, ErasureFormatV1.IntegrityBlockSize);
        _set.Members[dataMemberPosition]?.AddReconstructionBytes(ErasureFormatV1.IntegrityBlockSize);
        return shards[dataMemberPosition];
    }

    private async ValueTask WriteAtCoreAsync(
        ReadOnlyMemory<byte> source,
        long logicalOffset,
        CancellationToken cancellationToken)
    {
        int consumed = 0;
        long stripeWidth = checked(_set.Configuration.DataShardCount * (long)_set.Configuration.ShardSize);
        while (consumed < source.Length)
        {
            long offset = logicalOffset + consumed;
            int withinStripe = (int)(offset % stripeWidth);
            int count = (int)Math.Min(source.Length - consumed, stripeWidth - withinStripe);
            await WriteStripeAsync(source.Slice(consumed, count), offset, cancellationToken).ConfigureAwait(false);
            consumed += count;
        }
    }

    private async ValueTask WriteStripeAsync(
        ReadOnlyMemory<byte> source,
        long logicalOffset,
        CancellationToken cancellationToken)
    {
        long stripeWidth = checked(_set.Configuration.DataShardCount * (long)_set.Configuration.ShardSize);
        ulong stripeIndex = checked((ulong)(logicalOffset / stripeWidth));
        int stripeOffset = (int)(logicalOffset % stripeWidth);
        ErasureStripeGeneration? current = await ErasureStripeMetadataReader.ReadCurrentAsync(
            _set,
            stripeIndex,
            cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            Fault<object>("The stripe cannot establish a current generation for writing.");
        }

        int[] updatable = Enumerable.Range(0, _set.MemberCount)
            .Where(position => current!.Members[position] is not null && _set.Members[position]?.CanWrite == true)
            .ToArray();
        if (updatable.Length < _set.WriteQuorum)
        {
            throw new IOException("The stripe does not have enough current writable members for write quorum.");
        }

        var changes = new Dictionary<(int DataMember, uint BlockOffset), List<(int Offset, ReadOnlyMemory<byte> Bytes)>>();
        int sourceOffset = 0;
        while (sourceOffset < source.Length)
        {
            int absolute = stripeOffset + sourceOffset;
            int dataMember = absolute / ShardSize;
            int withinShard = absolute % ShardSize;
            uint blockOffset = (uint)(withinShard / ErasureFormatV1.IntegrityBlockSize * ErasureFormatV1.IntegrityBlockSize);
            int withinBlock = withinShard % ErasureFormatV1.IntegrityBlockSize;
            int count = Math.Min(source.Length - sourceOffset, ErasureFormatV1.IntegrityBlockSize - withinBlock);
            var key = (dataMember, blockOffset);
            if (!changes.TryGetValue(key, out List<(int Offset, ReadOnlyMemory<byte> Bytes)>? list))
            {
                list = [];
                changes.Add(key, list);
            }

            list.Add((withinBlock, source.Slice(sourceOffset, count)));
            sourceOffset += count;
        }

        uint[] affectedOffsets = changes.Keys.Select(static key => key.BlockOffset).Distinct().Order().ToArray();
        var finalBlocks = new Dictionary<(int Member, uint Offset), byte[]>();
        foreach (uint blockOffset in affectedOffsets)
        {
            var shards = new byte[_set.MemberCount][];
            for (int data = 0; data < DataShardCount; data++)
            {
                shards[data] = await ReadDataBlockFromGenerationAsync(
                    current!,
                    stripeIndex,
                    data,
                    blockOffset,
                    cancellationToken).ConfigureAwait(false);
                if (changes.TryGetValue((data, blockOffset), out List<(int Offset, ReadOnlyMemory<byte> Bytes)>? writes))
                {
                    foreach ((int offset, ReadOnlyMemory<byte> bytes) in writes)
                    {
                        bytes.CopyTo(shards[data].AsMemory(offset));
                    }

                    finalBlocks[(data, blockOffset)] = shards[data];
                }
            }

            for (int parity = DataShardCount; parity < _set.MemberCount; parity++)
            {
                shards[parity] = new byte[ErasureFormatV1.IntegrityBlockSize];
            }

            _codec.Encode(shards, 0, ErasureFormatV1.IntegrityBlockSize);
            for (int parity = DataShardCount; parity < _set.MemberCount; parity++)
            {
                finalBlocks[(parity, blockOffset)] = shards[parity];
            }
        }

        ulong sequence = _set.NextTransactionSequence;
        if (sequence == ulong.MaxValue)
        {
            throw new IOException("The erasure journal transaction sequence is exhausted.");
        }

        _set.NextTransactionSequence++;
        Guid transactionId = Guid.NewGuid();
        Guid generationId = Guid.NewGuid();
        int slot = checked((int)((sequence - 1) % (ulong)(_set.Layout.JournalLength / _set.Layout.JournalSlotSize)));
        var pending = new Dictionary<int, PendingMemberTransaction>();
        foreach (int member in updatable)
        {
            MemberAfterImage afterImage = CreateAfterImage(member, finalBlocks);
            UInt128 payloadHash = ErasureJournalPreparePageSerializer.ComputeLocalPayloadHash(afterImage.Payload);
            var prepare = new ErasureJournalPreparePage(
                TransactionFlags: 0,
                TransactionSequence: sequence,
                TransactionId: transactionId,
                SetId: _set.Configuration.SetId,
                ConfigurationId: _set.Configuration.ConfigurationId,
                ConfigurationGeneration: _set.Configuration.ConfigurationGeneration,
                StripeIndex: stripeIndex,
                StripeGenerationId: generationId,
                MemberPosition: (ushort)member,
                LocalPayloadLength: (uint)afterImage.Payload.Length,
                LocalPayloadHash: payloadHash);
            var prepareBytes = new byte[ErasureFormatV1.PageSize];
            UInt128 prepareHash = ErasureJournalPreparePageSerializer.Write(
                prepare,
                afterImage.Ranges,
                afterImage.Payload,
                ShardSize,
                prepareBytes);
            var commit = new ErasureJournalCommitPage(
                ErasureJournalCommitState.Committed,
                sequence,
                transactionId,
                _set.Configuration.SetId,
                _set.Configuration.ConfigurationId,
                stripeIndex,
                generationId,
                (ushort)member,
                prepareHash,
                payloadHash);
            var commitBytes = new byte[ErasureFormatV1.PageSize];
            ErasureJournalCommitPageSerializer.Write(commit, commitBytes);
            pending.Add(member, new PendingMemberTransaction(
                prepare,
                afterImage,
                prepareBytes,
                prepareHash,
                commitBytes,
                slot));
        }

        List<int> prepared = await WritePreparePhaseAsync(pending, cancellationToken).ConfigureAwait(false);
        if (prepared.Count < _set.WriteQuorum)
        {
            throw new IOException("The journal prepare phase did not reach write quorum.");
        }

        List<int> committed = await WriteCommitPhaseAsync(pending, prepared, cancellationToken).ConfigureAwait(false);
        if (committed.Count < _set.WriteQuorum)
        {
            throw new IOException("The journal commit phase did not reach write quorum; no home writes were started.");
        }

        List<int> home = await WriteHomePhaseAsync(
            pending,
            committed,
            current!,
            sequence,
            generationId,
            CancellationToken.None).ConfigureAwait(false);
        if (home.Count < _set.WriteQuorum)
        {
            await RecoverCommittedJournalAsync(CancellationToken.None).ConfigureAwait(false);
            throw new IOException("The committed write required journal replay after member failures.");
        }

        await WriteCheckpointPhaseAsync(pending, committed, CancellationToken.None).ConfigureAwait(false);
        foreach (int member in updatable)
        {
            if (!home.Contains(member))
            {
                _set.Members[member]!.Condition = ErasureMemberDeviceCondition.Stale;
            }
        }
    }

    private async ValueTask<byte[]> ReadDataBlockFromGenerationAsync(
        ErasureStripeGeneration generation,
        ulong stripeIndex,
        int dataMember,
        uint blockOffset,
        CancellationToken cancellationToken)
    {
        if (generation.TransactionSequence == 0)
        {
            return new byte[ErasureFormatV1.IntegrityBlockSize];
        }

        var tasks = new Task<byte[]?>[_set.MemberCount];
        for (int member = 0; member < tasks.Length; member++)
        {
            ErasureStripeMemberMetadata? metadata = generation.Members[member];
            int captured = member;
            tasks[member] = metadata is null
                ? Task.FromResult<byte[]?>(null)
                : ErasureStripeMetadataReader.ReadValidatedBlockAsync(
                    _set,
                    captured,
                    stripeIndex,
                    metadata,
                    blockOffset,
                    cancellationToken).AsTask();
        }

        byte[]?[] blocks = await Task.WhenAll(tasks).ConfigureAwait(false);
        var shards = new byte[_set.MemberCount][];
        var present = new bool[_set.MemberCount];
        for (int member = 0; member < shards.Length; member++)
        {
            shards[member] = blocks[member] ?? new byte[ErasureFormatV1.IntegrityBlockSize];
            present[member] = blocks[member] is not null;
        }

        if (present.Count(static value => value) < _set.ReadQuorum)
        {
            return Fault<byte[]>("A write could not reconstruct the current data block.");
        }

        _codec.Reconstruct(shards, present, 0, ErasureFormatV1.IntegrityBlockSize);
        return shards[dataMember];
    }

    private static MemberAfterImage CreateAfterImage(
        int memberPosition,
        Dictionary<(int Member, uint Offset), byte[]> finalBlocks)
    {
        (uint Offset, byte[] Payload)[] blocks = finalBlocks
            .Where(pair => pair.Key.Member == memberPosition)
            .Select(static pair => (pair.Key.Offset, pair.Value))
            .OrderBy(static pair => pair.Offset)
            .ToArray();
        if (blocks.Length == 0)
        {
            return new MemberAfterImage([], []);
        }

        var payload = new byte[checked(blocks.Length * ErasureFormatV1.IntegrityBlockSize)];
        var ranges = new List<ErasureJournalRange>();
        int payloadOffset = 0;
        foreach ((uint offset, byte[] block) in blocks)
        {
            block.CopyTo(payload, payloadOffset);
            if (ranges.Count != 0)
            {
                ErasureJournalRange previous = ranges[^1];
                if (previous.ShardOffset + previous.Length == offset)
                {
                    ranges[^1] = previous with { Length = previous.Length + ErasureFormatV1.IntegrityBlockSize };
                }
                else
                {
                    ranges.Add(new ErasureJournalRange(
                        offset,
                        ErasureFormatV1.IntegrityBlockSize,
                        (uint)payloadOffset,
                        0));
                }
            }
            else
            {
                ranges.Add(new ErasureJournalRange(
                    offset,
                    ErasureFormatV1.IntegrityBlockSize,
                    (uint)payloadOffset,
                    0));
            }

            payloadOffset += block.Length;
        }

        return new MemberAfterImage(ranges.ToArray(), payload);
    }

    private async ValueTask<List<int>> WritePreparePhaseAsync(
        IReadOnlyDictionary<int, PendingMemberTransaction> pending,
        CancellationToken cancellationToken)
    {
        byte[] emptyCommit = new byte[ErasureFormatV1.PageSize];
        (int Position, bool Success)[] results = await Task.WhenAll(
            pending.Select(pair => ExecuteAsync(pair.Key, pair.Value))).ConfigureAwait(false);
        return results.Where(static result => result.Success).Select(static result => result.Position).ToList();

        async Task<(int Position, bool Success)> ExecuteAsync(
            int position,
            PendingMemberTransaction transaction)
        {
            ErasureMemberDevice member = _set.Members[position]!;
            long slotOffset = GetJournalSlotOffset(transaction.Slot);
            long commitOffset = slotOffset + _set.Layout.JournalSlotSize - ErasureFormatV1.PageSize;
            try
            {
                await member.WriteAtAsync(emptyCommit, commitOffset, cancellationToken).ConfigureAwait(false);
                await member.FlushAsync(cancellationToken).ConfigureAwait(false);
                await member.WriteAtAsync(transaction.PrepareBytes, slotOffset, cancellationToken).ConfigureAwait(false);
                if (transaction.AfterImage.Payload.Length != 0)
                {
                    await member.WriteAtAsync(
                        transaction.AfterImage.Payload,
                        slotOffset + ErasureFormatV1.PageSize,
                        cancellationToken).ConfigureAwait(false);
                }

                await member.FlushAsync(cancellationToken).ConfigureAwait(false);
                return (position, true);
            }
            catch (Exception exception) when (IsMemberIoFailure(exception))
            {
                member.Condition = ErasureMemberDeviceCondition.Missing;
                return (position, false);
            }
        }
    }

    private async ValueTask<List<int>> WriteCommitPhaseAsync(
        IReadOnlyDictionary<int, PendingMemberTransaction> pending,
        IReadOnlyList<int> prepared,
        CancellationToken cancellationToken)
    {
        (int Position, bool Success)[] results = await Task.WhenAll(prepared.Select(ExecuteAsync)).ConfigureAwait(false);
        return results.Where(static result => result.Success).Select(static result => result.Position).ToList();

        async Task<(int Position, bool Success)> ExecuteAsync(int position)
        {
            ErasureMemberDevice member = _set.Members[position]!;
            PendingMemberTransaction transaction = pending[position];
            long commitOffset = GetJournalSlotOffset(transaction.Slot) +
                _set.Layout.JournalSlotSize - ErasureFormatV1.PageSize;
            try
            {
                await member.WriteAtAsync(transaction.CommitBytes, commitOffset, cancellationToken).ConfigureAwait(false);
                await member.FlushAsync(cancellationToken).ConfigureAwait(false);
                return (position, true);
            }
            catch (Exception exception) when (IsMemberIoFailure(exception))
            {
                member.Condition = ErasureMemberDeviceCondition.Missing;
                return (position, false);
            }
        }
    }

    private async ValueTask<List<int>> WriteHomePhaseAsync(
        IReadOnlyDictionary<int, PendingMemberTransaction> pending,
        IReadOnlyList<int> committed,
        ErasureStripeGeneration previousGeneration,
        ulong sequence,
        Guid generationId,
        CancellationToken cancellationToken)
    {
        (int Position, bool Success)[] results = await Task.WhenAll(committed.Select(ExecuteAsync)).ConfigureAwait(false);
        return results.Where(static result => result.Success).Select(static result => result.Position).ToList();

        async Task<(int Position, bool Success)> ExecuteAsync(int position)
        {
            ErasureMemberDevice member = _set.Members[position]!;
            PendingMemberTransaction transaction = pending[position];
            ErasureStripeMemberMetadata metadata = previousGeneration.Members[position]!;
            ulong[] checksums = metadata.IsImplicitZero
                ? CreateZeroChecksums()
                : (ulong[])metadata.Checksums.Clone();
            long recordOffset = ErasureStripeMetadataReader.GetShardRecordOffset(
                _set,
                transaction.Prepare.StripeIndex);
            try
            {
                foreach (ErasureJournalRange range in transaction.AfterImage.Ranges)
                {
                    int payloadOffset = checked((int)range.PayloadOffset);
                    int length = checked((int)range.Length);
                    ReadOnlyMemory<byte> payload = transaction.AfterImage.Payload.AsMemory(payloadOffset, length);
                    await member.WriteAtAsync(
                        payload,
                        checked(recordOffset + ErasureFormatV1.ShardHeaderSize + range.ShardOffset),
                        cancellationToken).ConfigureAwait(false);
                    for (int offset = 0; offset < length; offset += ErasureFormatV1.IntegrityBlockSize)
                    {
                        int checksumIndex = checked((int)((range.ShardOffset + (uint)offset) / ErasureFormatV1.IntegrityBlockSize));
                        checksums[checksumIndex] = XxHash64.HashToUInt64(
                            payload.Span.Slice(offset, ErasureFormatV1.IntegrityBlockSize));
                    }
                }

                var header = new ErasureShardHeader(
                    ShardFlags: 0,
                    ConfigurationGeneration: _set.Configuration.ConfigurationGeneration,
                    ConfigurationId: _set.Configuration.ConfigurationId,
                    StripeIndex: transaction.Prepare.StripeIndex,
                    TransactionSequence: sequence,
                    StripeGenerationId: generationId,
                    MemberPosition: (ushort)position,
                    StoredPayloadLength: _set.Configuration.ShardSize);
                var page = new byte[ErasureFormatV1.ShardHeaderSize];
                ErasureShardHeaderSerializer.Write(header, checksums, page);
                await member.WriteAtAsync(page, recordOffset, cancellationToken).ConfigureAwait(false);
                await member.FlushAsync(cancellationToken).ConfigureAwait(false);
                member.Condition = ErasureMemberDeviceCondition.Online;
                return (position, true);
            }
            catch (Exception exception) when (IsMemberIoFailure(exception))
            {
                member.Condition = ErasureMemberDeviceCondition.Missing;
                return (position, false);
            }
        }
    }

    private async ValueTask WriteCheckpointPhaseAsync(
        IReadOnlyDictionary<int, PendingMemberTransaction> pending,
        IReadOnlyList<int> committed,
        CancellationToken cancellationToken)
    {
        await Task.WhenAll(committed.Select(ExecuteAsync)).ConfigureAwait(false);

        async Task ExecuteAsync(int position)
        {
            ErasureMemberDevice member = _set.Members[position]!;
            PendingMemberTransaction transaction = pending[position];
            ErasureJournalPreparePage prepare = transaction.Prepare;
            var checkpoint = new ErasureJournalCommitPage(
                ErasureJournalCommitState.Checkpointed,
                prepare.TransactionSequence,
                prepare.TransactionId,
                prepare.SetId,
                prepare.ConfigurationId,
                prepare.StripeIndex,
                prepare.StripeGenerationId,
                prepare.MemberPosition,
                transaction.PrepareHash,
                prepare.LocalPayloadHash);
            var page = new byte[ErasureFormatV1.PageSize];
            ErasureJournalCommitPageSerializer.Write(checkpoint, page);
            long commitOffset = GetJournalSlotOffset(transaction.Slot) +
                _set.Layout.JournalSlotSize - ErasureFormatV1.PageSize;
            try
            {
                await member.WriteAtAsync(page, commitOffset, cancellationToken).ConfigureAwait(false);
                await member.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsMemberIoFailure(exception))
            {
                member.Condition = ErasureMemberDeviceCondition.Missing;
            }
        }
    }

    private async ValueTask RecoverCommittedJournalAsync(CancellationToken cancellationToken)
    {
        ErasureJournalSlotScanResult scan = await ErasureJournalSlotScanner.ScanAsync(_set, cancellationToken).ConfigureAwait(false);
        if (!scan.Transactions.IsSuccess)
        {
            Fault<object>($"Committed journal recovery failed: {scan.Transactions.State}.");
        }

        await ErasureJournalReplayExecutor.ReplayAsync(
            _set,
            scan.Transactions.Transactions,
            _codec,
            cancellationToken).ConfigureAwait(false);
    }

    private ulong[] CreateZeroChecksums()
    {
        int count = ShardSize / ErasureFormatV1.IntegrityBlockSize;
        ulong zeroHash = XxHash64.HashToUInt64(new byte[ErasureFormatV1.IntegrityBlockSize]);
        return Enumerable.Repeat(zeroHash, count).ToArray();
    }

    private long GetJournalSlotOffset(int slot) =>
        checked(_set.Layout.JournalOffset + slot * _set.Layout.JournalSlotSize);

    private LogicalBlockAddress MapLogicalOffset(long logicalOffset)
    {
        long stripeWidth = checked(DataShardCount * (long)ShardSize);
        ulong stripeIndex = checked((ulong)(logicalOffset / stripeWidth));
        int withinStripe = (int)(logicalOffset % stripeWidth);
        int dataPosition = withinStripe / ShardSize;
        int withinShard = withinStripe % ShardSize;
        uint blockOffset = (uint)(withinShard / ErasureFormatV1.IntegrityBlockSize * ErasureFormatV1.IntegrityBlockSize);
        return new LogicalBlockAddress(
            stripeIndex,
            dataPosition,
            blockOffset,
            withinShard % ErasureFormatV1.IntegrityBlockSize);
    }

    private static Stream[] MaterializeMembers(IEnumerable<Stream> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return members.ToArray();
    }

    private void ValidateOffset(long offset, bool allowEnd)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        long maximum = checked((long)_set.Configuration.LogicalCapacity);
        if (allowEnd ? offset > maximum : offset >= maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
    }

    private void ValidateWriteRange(long offset, int count)
    {
        ValidateOffset(offset, allowEnd: count == 0);
        if (count > (long)_set.Configuration.LogicalCapacity - offset)
        {
            throw new IOException("The write exceeds the fixed logical capacity.");
        }
    }

    private static void ValidateArrayRange(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - offset < count)
        {
            throw new ArgumentException("Offset and count must identify a range within the buffer.");
        }
    }

    private T Fault<T>(string message)
    {
        var exception = new ErasureCodeStreamCorruptionException(message);
        _fault = exception;
        PublishStateIfChanged();
        throw exception;
    }

    private void ThrowIfUnavailable()
    {
        ThrowIfDisposed();
        if (_fault is not null)
        {
            throw new ErasureCodeStreamCorruptionException("The erasure stream is faulted.", _fault);
        }
    }

    private void ThrowIfCannotWrite()
    {
        ThrowIfUnavailable();
        if (_options.ReadOnly)
        {
            throw new NotSupportedException("The erasure stream is read-only.");
        }

        if (WritableMemberCount < _set.WriteQuorum)
        {
            throw new IOException("The erasure stream does not have write quorum.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static bool IsMemberIoFailure(Exception exception) =>
        exception is IOException or NotSupportedException or ObjectDisposedException;

    private readonly record struct LogicalBlockAddress(
        ulong StripeIndex,
        int DataMemberPosition,
        uint BlockOffset,
        int OffsetWithinBlock);

    private sealed record MemberAfterImage(
        ErasureJournalRange[] Ranges,
        byte[] Payload);

    private sealed record PendingMemberTransaction(
        ErasureJournalPreparePage Prepare,
        MemberAfterImage AfterImage,
        byte[] PrepareBytes,
        UInt128 PrepareHash,
        byte[] CommitBytes,
        int Slot);

    private sealed class CallbackRegistration : IDisposable
    {
        private Action? _unregister;

        internal CallbackRegistration(Action unregister) => _unregister = unregister;

        public void Dispose() => Interlocked.Exchange(ref _unregister, null)?.Invoke();
    }
}
