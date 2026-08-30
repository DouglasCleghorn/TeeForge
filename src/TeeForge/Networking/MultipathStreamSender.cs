using TeeForge.ErasureCoding.Internal;
using TeeForge.Networking.Internal;

namespace TeeForge.Networking;

/// <summary>Distributes one writable logical byte stream across dynamically managed data paths.</summary>
/// <remarks>
/// Calls that write, flush, complete, or change mode are serialized. Paths may be added while a
/// write is waiting for connectivity. Call <see cref="CompleteAsync"/> to publish logical end of stream;
/// disposal without completion aborts pending data. The sender owns supplied paths unless its options
/// leave them open.
/// </remarks>
public class MultipathSenderStream : Stream
{
    private readonly object _stateLock = new();
    private readonly Dictionary<Guid, MultipathSenderPath> _paths = [];
    private readonly MultipathStreamOptions _options;
    private readonly SemaphoreSlim _membershipGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private TaskCompletionSource<bool> _pathAvailable = CreateAvailabilitySource();
    private byte[] _pending = [];
    private int _pendingCount;
    private MultipathStreamMode _desiredMode;
    private int _dataShardCount;
    private int _parityShardCount;
    private ulong _epoch;
    private ulong _sequence;
    private Exception? _fault;
    private int _disposeState;
    private int _completeState;

    /// <summary>Initializes a sender with a newly generated session identifier.</summary>
    public MultipathSenderStream()
        : this(Guid.NewGuid(), new MultipathStreamOptions())
    {
    }

    /// <summary>Initializes a sender with a newly generated session identifier and explicit options.</summary>
    public MultipathSenderStream(MultipathStreamOptions options)
        : this(Guid.NewGuid(), options)
    {
    }

    /// <summary>Initializes a sender with an explicit nonempty session identifier and options.</summary>
    public MultipathSenderStream(Guid sessionId, MultipathStreamOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(sessionId, Guid.Empty);
        SessionId = sessionId;
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _desiredMode = _options.Mode;
        _dataShardCount = _options.ErasureDataShardCount;
        _parityShardCount = _options.ErasureParityShardCount;
    }

    /// <summary>Gets the identifier included in every path and data frame for this session.</summary>
    public Guid SessionId { get; }

    /// <summary>Gets the currently requested distribution mode.</summary>
    public MultipathStreamMode DesiredMode
    {
        get
        {
            lock (_stateLock)
            {
                return _desiredMode;
            }
        }
    }

    /// <summary>Gets the mode that would be used for the next group with the current path count.</summary>
    public MultipathStreamMode EffectiveMode
    {
        get
        {
            lock (_stateLock)
            {
                return ResolveEffectiveMode(_paths.Count, _desiredMode, _dataShardCount, _parityShardCount);
            }
        }
    }

    /// <summary>Gets the number of active sender paths.</summary>
    public int PathCount
    {
        get
        {
            lock (_stateLock)
            {
                return _paths.Count;
            }
        }
    }

    /// <summary>Gets the configured erasure data-shard count.</summary>
    public int ErasureDataShardCount
    {
        get
        {
            lock (_stateLock)
            {
                return _dataShardCount;
            }
        }
    }

    /// <summary>Gets the configured erasure parity-shard count.</summary>
    public int ErasureParityShardCount
    {
        get
        {
            lock (_stateLock)
            {
                return _parityShardCount;
            }
        }
    }

    /// <inheritdoc/>
    public override bool CanRead => false;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => Volatile.Read(ref _disposeState) == 0 &&
        Volatile.Read(ref _completeState) == 0 && Volatile.Read(ref _fault) is null;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Adds a writable path and returns its generated path identifier.</summary>
    public ValueTask<Guid> AddPathAsync(
        Stream path,
        CancellationToken cancellationToken = default) =>
        AddPathCoreAsync(path, initializer: null, cancellationToken);

    /// <summary>Initializes and adds a writable path, then returns its generated path identifier.</summary>
    public ValueTask<Guid> AddPathAsync(
        Stream path,
        Func<Stream, CancellationToken, ValueTask> initializer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        return AddPathCoreAsync(path, initializer, cancellationToken);
    }

    /// <summary>Gracefully removes a path from groups created after this call.</summary>
    public async ValueTask<bool> RemovePathAsync(Guid pathId)
    {
        ThrowIfDisposed();
        lock (_stateLock)
        {
            if (!_paths.ContainsKey(pathId))
            {
                return false;
            }
        }

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            MultipathSenderPath? removed;
            ulong retireEpoch;
            lock (_stateLock)
            {
                if (!_paths.Remove(pathId, out removed))
                {
                    return false;
                }

                AdvanceEpochLocked();
                retireEpoch = _epoch;
                ResetAvailabilityIfEmptyLocked();
            }

            byte[] retire = MultipathProtocol.CreateRetireFrame(SessionId, retireEpoch);
            try
            {
                await removed.SendAsync(retire, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await removed.DisposeAsync().ConfigureAwait(false);
            }

            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Changes the desired mode at the next complete logical group.</summary>
    public async ValueTask ChangeModeAsync(
        MultipathStreamMode mode,
        int? erasureDataShardCount = null,
        int? erasureParityShardCount = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        int dataCount = erasureDataShardCount ?? ErasureDataShardCount;
        int parityCount = erasureParityShardCount ?? ErasureParityShardCount;
        ValidateErasureCounts(dataCount, parityCount);

        ThrowIfUnavailableForWrite();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ThrowIfUnavailableForWrite();
            await DrainPendingAsync(flushPartial: true, cancellationToken).ConfigureAwait(false);
            lock (_stateLock)
            {
                _desiredMode = mode;
                _dataShardCount = dataCount;
                _parityShardCount = parityCount;
                AdvanceEpochLocked();
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Flushes pending data and sends an end-of-stream marker.</summary>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _membershipGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (Interlocked.CompareExchange(ref _completeState, 1, 0) != 0)
            {
                return;
            }
        }
        finally
        {
            _membershipGate.Release();
        }

        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref _completeState, 0);
            throw;
        }

        try
        {
            await CompleteCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref _completeState, 0);
            throw;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc/>
    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc/>
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        ThrowIfUnavailableForWrite();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailableForWrite();
            await DrainPendingAsync(flushPartial: true, cancellationToken).ConfigureAwait(false);
            await FlushPathsAsync(DesiredMode == MultipathStreamMode.Raid0, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        byte[] copy = buffer.ToArray();
        WriteAsync(copy, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc/>
    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc/>
    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailableForWrite();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailableForWrite();
            ReadOnlyMemory<byte> remaining = buffer;
            while (!remaining.IsEmpty)
            {
                SenderSnapshot snapshot = await GetUsableSnapshotAsync(cancellationToken).ConfigureAwait(false);
                int capacity = GetLogicalGroupCapacity(snapshot);
                if (_pendingCount >= capacity)
                {
                    await SendPendingPrefixAsync(capacity, snapshot, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                EnsurePendingCapacity(capacity);
                int copyCount = Math.Min(capacity - _pendingCount, remaining.Length);
                remaining.Span[..copyCount].CopyTo(_pending.AsSpan(_pendingCount));
                _pendingCount += copyCount;
                remaining = remaining[copyCount..];

                if (_pendingCount == capacity)
                {
                    await SendPendingPrefixAsync(capacity, snapshot, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && Volatile.Read(ref _disposeState) == 0)
        {
            _membershipGate.Wait();
            if (Interlocked.Exchange(ref _disposeState, 1) == 0)
            {
                try
                {
                    SignalPathWaiters();
                    DisposePathsAsync().AsTask().GetAwaiter().GetResult();
                    if (!_options.LeaveOpen)
                    {
                        _writeGate.Wait();
                        _writeGate.Release();
                    }
                }
                finally
                {
                    _membershipGate.Dispose();
                }
            }
            else
            {
                _membershipGate.Release();
            }
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposeState) == 0)
        {
            await _membershipGate.WaitAsync().ConfigureAwait(false);
            if (Interlocked.Exchange(ref _disposeState, 1) == 0)
            {
                try
                {
                    SignalPathWaiters();
                    await DisposePathsAsync().ConfigureAwait(false);
                    if (!_options.LeaveOpen)
                    {
                        await _writeGate.WaitAsync().ConfigureAwait(false);
                        _writeGate.Release();
                    }
                }
                finally
                {
                    _membershipGate.Dispose();
                }
            }
            else
            {
                _membershipGate.Release();
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private static TaskCompletionSource<bool> CreateAvailabilitySource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async ValueTask CompleteCoreAsync(CancellationToken cancellationToken)
    {
        ThrowIfFaulted();
        await DrainPendingAsync(flushPartial: true, cancellationToken).ConfigureAwait(false);
        byte[] complete = MultipathProtocol.CreateCompleteFrame(SessionId, _sequence);
        await SendMirroredWithRetryAsync(complete, cancellationToken).ConfigureAwait(false);
        await FlushPathsAsync(requireAll: false, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Guid> AddPathCoreAsync(
        Stream path,
        Func<Stream, CancellationToken, ValueTask>? initializer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.CanWrite)
        {
            throw new ArgumentException("A sender path must be writable.", nameof(path));
        }

        ThrowIfUnavailableForWrite();
        await _membershipGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailableForWrite();
            if (initializer is not null)
            {
                await initializer(path, cancellationToken).ConfigureAwait(false);
            }

            Guid pathId = Guid.NewGuid();
            byte[] hello = MultipathProtocol.CreateHelloFrame(SessionId, pathId);
            await path.WriteAsync(hello, cancellationToken).ConfigureAwait(false);
            await path.FlushAsync(cancellationToken).ConfigureAwait(false);

            var state = new MultipathSenderPath(
                pathId,
                path,
                _options.PathQueueCapacity,
                _options.LeaveOpen);
            lock (_stateLock)
            {
                ThrowIfUnavailableForWrite();
                _paths.Add(pathId, state);
                AdvanceEpochLocked();
                _pathAvailable.TrySetResult(true);
            }

            return pathId;
        }
        finally
        {
            _membershipGate.Release();
        }
    }

    private async ValueTask DrainPendingAsync(bool flushPartial, CancellationToken cancellationToken)
    {
        while (_pendingCount > 0)
        {
            SenderSnapshot snapshot = await GetUsableSnapshotAsync(cancellationToken).ConfigureAwait(false);
            int capacity = GetLogicalGroupCapacity(snapshot);
            if (_pendingCount < capacity && !flushPartial)
            {
                return;
            }

            await SendPendingPrefixAsync(Math.Min(_pendingCount, capacity), snapshot, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask SendPendingPrefixAsync(
        int byteCount,
        SenderSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        byte[] payload = _pending.AsSpan(0, byteCount).ToArray();
        await SendGroupAsync(payload, snapshot, cancellationToken).ConfigureAwait(false);
        _pendingCount -= byteCount;
        if (_pendingCount > 0)
        {
            Buffer.BlockCopy(_pending, byteCount, _pending, 0, _pendingCount);
        }
    }

    private async ValueTask SendGroupAsync(
        byte[] logicalPayload,
        SenderSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.Mode == MultipathStreamMode.Raid1)
        {
            byte[] frame = MultipathProtocol.CreateDataFrame(
                SessionId,
                snapshot.Epoch,
                _sequence,
                MultipathStreamMode.Raid1,
                0,
                1,
                0,
                logicalPayload.Length,
                logicalPayload);
            await SendMirroredWithRetryAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        else if (snapshot.Mode == MultipathStreamMode.Raid0)
        {
            byte[] frame = MultipathProtocol.CreateDataFrame(
                SessionId,
                snapshot.Epoch,
                _sequence,
                MultipathStreamMode.Raid0,
                0,
                1,
                0,
                logicalPayload.Length,
                logicalPayload);
            int index = (int)(_sequence % (ulong)snapshot.Paths.Length);
            try
            {
                await snapshot.Paths[index].SendAsync(frame, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await FailPathAsync(snapshot.Paths[index]).ConfigureAwait(false);
                Fault(new IOException("A RAID-0 path failed after being assigned a logical group.", exception));
                ThrowIfFaulted();
            }
        }
        else
        {
            await SendErasureGroupAsync(logicalPayload, snapshot, cancellationToken).ConfigureAwait(false);
        }

        _sequence = checked(_sequence + 1);
    }

    private async ValueTask SendErasureGroupAsync(
        byte[] logicalPayload,
        SenderSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        int shardSize = _options.FramePayloadSize;
        int memberCount = snapshot.DataShardCount + snapshot.ParityShardCount;
        var codec = new ReedSolomonCodec(snapshot.DataShardCount, snapshot.ParityShardCount);
        var shards = new byte[memberCount][];
        for (int index = 0; index < shards.Length; index++)
        {
            shards[index] = new byte[shardSize];
        }

        int logicalOffset = 0;
        for (int shard = 0; shard < snapshot.DataShardCount && logicalOffset < logicalPayload.Length; shard++)
        {
            int copyCount = Math.Min(shardSize, logicalPayload.Length - logicalOffset);
            logicalPayload.AsSpan(logicalOffset, copyCount).CopyTo(shards[shard]);
            logicalOffset += copyCount;
        }

        codec.Encode(shards, 0, shardSize);
        var sends = new Task[memberCount];
        for (int shard = 0; shard < memberCount; shard++)
        {
            byte[] frame = MultipathProtocol.CreateDataFrame(
                SessionId,
                snapshot.Epoch,
                _sequence,
                MultipathStreamMode.ErasureCode,
                checked((byte)shard),
                checked((byte)snapshot.DataShardCount),
                checked((byte)snapshot.ParityShardCount),
                logicalPayload.Length,
                shards[shard]);
            int pathIndex = (int)((_sequence + (ulong)shard) % (ulong)snapshot.Paths.Length);
            sends[shard] = SendTrackedAsync(snapshot.Paths[pathIndex], frame, cancellationToken);
        }

        int successes = await AwaitSuccessCountAsync(sends, snapshot.DataShardCount).ConfigureAwait(false);
        if (successes < snapshot.DataShardCount)
        {
            Fault(new IOException("Too few erasure shards were accepted to preserve the logical group."));
            ThrowIfFaulted();
        }
    }

    private async ValueTask SendMirroredWithRetryAsync(byte[] frame, CancellationToken cancellationToken)
    {
        while (true)
        {
            SenderSnapshot snapshot = await GetUsableSnapshotAsync(cancellationToken).ConfigureAwait(false);
            Task[] sends = snapshot.Paths
                .Select(path => SendTrackedAsync(path, frame, cancellationToken))
                .ToArray();
            if (await AwaitSuccessCountAsync(sends, requiredSuccesses: 1).ConfigureAwait(false) >= 1)
            {
                return;
            }
        }
    }

    private async Task SendTrackedAsync(
        MultipathSenderPath path,
        byte[] frame,
        CancellationToken cancellationToken)
    {
        try
        {
            await path.SendAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await FailPathAsync(path).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<int> AwaitSuccessCountAsync(Task[] tasks, int requiredSuccesses)
    {
        var remaining = tasks.ToList();
        int successes = 0;
        while (remaining.Count > 0 && successes < requiredSuccesses)
        {
            Task completed = await Task.WhenAny(remaining).ConfigureAwait(false);
            remaining.Remove(completed);
            try
            {
                await completed.ConfigureAwait(false);
                successes++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
            }
        }

        foreach (Task task in remaining)
        {
            _ = ObserveAsync(task);
        }

        return successes;
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
        }
    }

    private async ValueTask FlushPathsAsync(bool requireAll, CancellationToken cancellationToken)
    {
        while (true)
        {
            SenderSnapshot snapshot = await GetUsableSnapshotAsync(cancellationToken).ConfigureAwait(false);
            Task[] flushes = snapshot.Paths
                .Select(path => FlushTrackedAsync(path, cancellationToken))
                .ToArray();
            int required = requireAll ? flushes.Length : 1;
            int successes = await AwaitSuccessCountAsync(flushes, required).ConfigureAwait(false);
            if (successes >= required)
            {
                return;
            }

            if (requireAll)
            {
                Fault(new IOException("A RAID-0 path failed while flushing the logical stream."));
                ThrowIfFaulted();
            }
        }
    }

    private async Task FlushTrackedAsync(
        MultipathSenderPath path,
        CancellationToken cancellationToken)
    {
        try
        {
            await path.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await FailPathAsync(path).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<SenderSnapshot> GetUsableSnapshotAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            ThrowIfDisposed();
            ThrowIfFaulted();
            Task waitTask;
            lock (_stateLock)
            {
                if (_paths.Count > 0)
                {
                    MultipathSenderPath[] paths = _paths.Values
                        .OrderBy(static path => path.PathId)
                        .ToArray();
                    MultipathStreamMode mode = ResolveEffectiveMode(
                        paths.Length,
                        _desiredMode,
                        _dataShardCount,
                        _parityShardCount);
                    return new SenderSnapshot(
                        paths,
                        _epoch,
                        mode,
                        _dataShardCount,
                        _parityShardCount);
                }

                waitTask = _pathAvailable.Task;
            }

            try
            {
                if (_options.PathAvailabilityTimeout == Timeout.InfiniteTimeSpan)
                {
                    await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await waitTask.WaitAsync(_options.PathAvailabilityTimeout, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (TimeoutException exception)
            {
                throw new IOException("No multipath data path became available before the configured timeout.", exception);
            }
        }
    }

    private async ValueTask FailPathAsync(MultipathSenderPath path)
    {
        bool removed;
        lock (_stateLock)
        {
            removed = _paths.Remove(path.PathId);
            if (removed)
            {
                AdvanceEpochLocked();
                ResetAvailabilityIfEmptyLocked();
            }
        }

        if (removed)
        {
            await path.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask DisposePathsAsync()
    {
        MultipathSenderPath[] paths;
        lock (_stateLock)
        {
            paths = _paths.Values.ToArray();
            _paths.Clear();
            ResetAvailabilityIfEmptyLocked();
        }

        foreach (MultipathSenderPath path in paths)
        {
            await path.DisposeAsync().ConfigureAwait(false);
        }
    }

    private int GetLogicalGroupCapacity(SenderSnapshot snapshot) =>
        snapshot.Mode == MultipathStreamMode.ErasureCode
            ? checked(_options.FramePayloadSize * snapshot.DataShardCount)
            : _options.FramePayloadSize;

    private void EnsurePendingCapacity(int capacity)
    {
        if (_pending.Length < capacity)
        {
            Array.Resize(ref _pending, capacity);
        }
    }

    private void AdvanceEpochLocked() => _epoch = checked(_epoch + 1);

    private void ResetAvailabilityIfEmptyLocked()
    {
        if (_paths.Count == 0 && _pathAvailable.Task.IsCompleted)
        {
            _pathAvailable = CreateAvailabilitySource();
        }
    }

    private void SignalPathWaiters()
    {
        lock (_stateLock)
        {
            _pathAvailable.TrySetResult(true);
        }
    }

    private void Fault(Exception exception) => Interlocked.CompareExchange(ref _fault, exception, null);

    private void ThrowIfFaulted()
    {
        Exception? fault = Volatile.Read(ref _fault);
        if (fault is not null)
        {
            throw new IOException("The multipath sender is faulted.", fault);
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);

    private void ThrowIfUnavailableForWrite()
    {
        ThrowIfDisposed();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _completeState) != 0, this);
        ThrowIfFaulted();
    }

    private static MultipathStreamMode ResolveEffectiveMode(
        int pathCount,
        MultipathStreamMode desiredMode,
        int dataShardCount,
        int parityShardCount) =>
        desiredMode == MultipathStreamMode.ErasureCode && pathCount < dataShardCount + parityShardCount
            ? MultipathStreamMode.Raid1
            : desiredMode;

    private static void ValidateErasureCounts(int dataShardCount, int parityShardCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dataShardCount, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(parityShardCount, 1);
        if ((long)dataShardCount + parityShardCount > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(parityShardCount));
        }
    }

    private readonly record struct SenderSnapshot(
        MultipathSenderPath[] Paths,
        ulong Epoch,
        MultipathStreamMode Mode,
        int DataShardCount,
        int ParityShardCount);
}
