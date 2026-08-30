using System.Buffers;
using TeeForge.RandomAccess;

namespace TeeForge.Composition;

/// <summary>
/// Moves a live, seekable byte sequence from a source stream to a destination stream.
/// </summary>
/// <remarks>
/// Migration proceeds from offset zero in bounded background quanta. Position-based foreground
/// operations are serialized, and queued foreground operations run before the next migration
/// quantum. Reads use the destination for the migrated prefix and the source for the remaining
/// suffix. Writes are applied source-first to both streams until migration completes. If migration
/// fails or is canceled, later operations continue against the source.
/// </remarks>
public class MigratingStream : Stream, ITeeRandomAccessStream
{
    private readonly Stream _source;
    private readonly Stream _destination;
    private readonly ITeeRandomAccessStream? _sourceRandomAccess;
    private readonly ITeeRandomAccessStream? _destinationRandomAccess;
    private readonly MigratingStreamOptions _options;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _migrationCancellation;
    private readonly TaskCompletionSource _migrationCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _migrationWorker = Task.CompletedTask;
    private long _logicalLength;
    private long _migratedLength;
    private long _position;
    private int _foregroundWaiters;
    private int _state = (int)MigrationState.Active;
    private int _started;
    private int _leaveSourceOpen;
    private int _leaveDestinationOpen;
    private int _disposed;

    /// <summary>Initializes a live migration from <paramref name="source"/> to <paramref name="destination"/>.</summary>
    /// <param name="source">The readable, writable, seekable authoritative source.</param>
    /// <param name="destination">The readable, writable, seekable migration destination.</param>
    /// <param name="options">Migration and ownership options.</param>
    /// <param name="cancellationToken">The token that cancels background migration.</param>
    public MigratingStream(
        Stream source,
        Stream destination,
        MigratingStreamOptions? options = null,
        CancellationToken cancellationToken = default)
        : this(
            source,
            destination,
            options,
            startMigration: true,
            cancellationToken: cancellationToken)
    {
    }

    internal MigratingStream(
        Stream source,
        Stream destination,
        MigratingStreamOptions? options,
        bool startMigration,
        CancellationToken cancellationToken)
    {
        ValidateStreams(source, destination);
        _source = source;
        _destination = destination;
        _options = options ?? MigratingStreamOptions.Default;
        _leaveSourceOpen = _options.LeaveSourceOpen ? 1 : 0;
        _leaveDestinationOpen = _options.LeaveDestinationOpen ? 1 : 0;
        TeeRandomAccess.TryGet(source, out _sourceRandomAccess);
        TeeRandomAccess.TryGet(destination, out _destinationRandomAccess);
        _logicalLength = source.Length;
        _position = source.Position;
        destination.SetLength(_logicalLength);
        _migrationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (startMigration)
        {
            StartMigration();
        }
    }

    /// <summary>Gets the options used by this stream.</summary>
    public MigratingStreamOptions Options => _options;

    /// <summary>Gets a task that represents copying, destination flush, and optional source truncation.</summary>
    /// <remarks>
    /// The task faults on copy failure and is canceled when the supplied token or disposal cancels
    /// migration. The wrapper remains usable against the source after either outcome. If optional
    /// source cleanup alone fails after destination activation, the complete destination remains
    /// authoritative and the task reports the cleanup failure.
    /// </remarks>
    public Task MigrationCompletion => _migrationCompletion.Task;

    internal bool DestinationIsAuthoritative => State == MigrationState.Completed;

    internal void ReleaseSourceOwnership() => Volatile.Write(ref _leaveSourceOpen, 1);

    internal void ReleaseDestinationOwnership() => Volatile.Write(ref _leaveDestinationOpen, 1);

    internal void StartMigration()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("Migration has already started.");
        }

        _migrationWorker = Task.Run(RunMigrationAsync, CancellationToken.None);
    }

    /// <inheritdoc/>
    public override bool CanRead => !IsDisposed;

    /// <inheritdoc/>
    public override bool CanSeek => !IsDisposed;

    /// <inheritdoc/>
    public override bool CanWrite => !IsDisposed;

    /// <inheritdoc/>
    public bool CanReadAt => !IsDisposed;

    /// <inheritdoc/>
    public bool CanWriteAt => !IsDisposed;

    /// <inheritdoc/>
    public override long Length
    {
        get
        {
            EnterForeground();
            try
            {
                return _logicalLength;
            }
            finally
            {
                ExitOperation();
            }
        }
    }

    /// <inheritdoc/>
    public override long Position
    {
        get
        {
            EnterForeground();
            try
            {
                return _position;
            }
            finally
            {
                ExitOperation();
            }
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            EnterForeground();
            try
            {
                _position = value;
            }
            finally
            {
                ExitOperation();
            }
        }
    }

    /// <inheritdoc/>
    public override void Flush()
    {
        EnterForeground();
        try
        {
            FlushCore();
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <inheritdoc/>
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await EnterForegroundAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        EnterForeground();
        try
        {
            int read = ReadAtCore(buffer, _position);
            _position += read;
            return read;
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <inheritdoc/>
    public override int ReadByte()
    {
        Span<byte> value = stackalloc byte[1];
        return Read(value) == 0 ? -1 : value[0];
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadArrayAsync(buffer, offset, count, cancellationToken);
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        await EnterForegroundAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int read = await ReadAtCoreAsync(buffer, _position, cancellationToken)
                .ConfigureAwait(false);
            _position += read;
            return read;
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <inheritdoc/>
    public int ReadAt(Span<byte> buffer, long offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        EnterForeground();
        try
        {
            return ReadAtCore(buffer, offset);
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <inheritdoc/>
    public async ValueTask<int> ReadAtAsync(
        Memory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        await EnterForegroundAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAtCoreAsync(buffer, offset, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ExitOperation();
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
        EnterForeground();
        try
        {
            WriteAtCore(buffer, _position);
            _position = checked(_position + buffer.Length);
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <inheritdoc/>
    public override void WriteByte(byte value)
    {
        ReadOnlySpan<byte> buffer = new(in value);
        Write(buffer);
    }

    /// <inheritdoc/>
    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteArrayAsync(buffer, offset, count, cancellationToken);
    }

    /// <inheritdoc/>
    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        await EnterForegroundAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAtCoreAsync(buffer, _position, cancellationToken).ConfigureAwait(false);
            _position = checked(_position + buffer.Length);
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <inheritdoc/>
    public void WriteAt(ReadOnlySpan<byte> buffer, long offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        EnterForeground();
        try
        {
            WriteAtCore(buffer, offset);
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <inheritdoc/>
    public async ValueTask WriteAtAsync(
        ReadOnlyMemory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        await EnterForegroundAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAtCoreAsync(buffer, offset, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        EnterForeground();
        try
        {
            long basis = origin switch
            {
                SeekOrigin.Begin => 0,
                SeekOrigin.Current => _position,
                SeekOrigin.End => _logicalLength,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            long position = checked(basis + offset);
            if (position < 0)
            {
                throw new IOException("An attempt was made to seek before the beginning of the stream.");
            }

            _position = position;
            return position;
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <inheritdoc/>
    public override void SetLength(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        EnterForeground();
        try
        {
            SetLengthCore(value);
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <inheritdoc/>
    public override void CopyTo(Stream destination, int bufferSize)
    {
        ValidateCopyToArguments(destination, bufferSize);
        EnterForeground();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            while (true)
            {
                int read = ReadAtCore(buffer.AsSpan(0, bufferSize), _position);
                if (read == 0)
                {
                    break;
                }

                destination.Write(buffer, 0, read);
                _position += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ExitOperation();
        }
    }

    /// <inheritdoc/>
    public override async Task CopyToAsync(
        Stream destination,
        int bufferSize,
        CancellationToken cancellationToken)
    {
        ValidateCopyToArguments(destination, bufferSize);
        await EnterForegroundAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            while (true)
            {
                int read = await ReadAtCoreAsync(
                        buffer.AsMemory(0, bufferSize),
                        _position,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                _position += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ExitOperation();
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!disposing || Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            base.Dispose(disposing);
            return;
        }

        _migrationCancellation.Cancel();
        CancelUnstartedMigration();
        _migrationWorker.GetAwaiter().GetResult();
        _operationGate.Wait();
        try
        {
            DisposeStreams();
        }
        finally
        {
            _operationGate.Release();
            _migrationCancellation.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await base.DisposeAsync().ConfigureAwait(false);
            return;
        }

        _migrationCancellation.Cancel();
        CancelUnstartedMigration();
        await _migrationWorker.ConfigureAwait(false);
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeStreamsAsync().ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
            _migrationCancellation.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private MigrationState State => (MigrationState)Volatile.Read(ref _state);

    private void CancelUnstartedMigration()
    {
        if (Volatile.Read(ref _started) == 0)
        {
            FailMigration(_migrationCancellation.Token);
        }
    }

    private async Task RunMigrationAsync()
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_options.BufferSize);
        CancellationToken cancellationToken = _migrationCancellation.Token;
        try
        {
            while (true)
            {
                await EnterBackgroundAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (State != MigrationState.Active)
                    {
                        return;
                    }

                    if (_migratedLength >= _logicalLength)
                    {
                        await CompleteMigrationAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    int count = (int)Math.Min(_options.BufferSize, _logicalLength - _migratedLength);
                    int read = await ReadExactlyAtAsync(
                            _source,
                            buffer.AsMemory(0, count),
                            _migratedLength,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (read != count)
                    {
                        throw new EndOfStreamException("The migration source ended before its reported length.");
                    }

                    await WritePhysicalAsync(
                            _destination,
                            buffer.AsMemory(0, count),
                            _migratedLength,
                            cancellationToken)
                        .ConfigureAwait(false);
                    _migratedLength += count;
                }
                finally
                {
                    ExitOperation();
                }

                await Task.Yield();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            FailMigration(cancellationToken);
        }
        catch (Exception exception)
        {
            FailMigration(exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask CompleteMigrationAsync(CancellationToken cancellationToken)
    {
        await _destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _state, (int)MigrationState.Completed);
        if (_options.TruncateSourceOnCompletion)
        {
            try
            {
                _source.SetLength(0);
                await _source.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _migrationCompletion.TrySetException(exception);
                return;
            }
        }

        _migrationCompletion.TrySetResult();
    }

    private int ReadAtCore(Span<byte> buffer, long offset)
    {
        if (buffer.IsEmpty || offset >= _logicalLength)
        {
            return 0;
        }

        int count = (int)Math.Min(buffer.Length, _logicalLength - offset);
        Stream stream = SelectReadStream(offset, out long available);
        count = (int)Math.Min(count, available);
        try
        {
            return ReadPhysical(stream, buffer[..count], offset);
        }
        catch (Exception exception) when (ReferenceEquals(stream, _destination) && State == MigrationState.Active)
        {
            FailMigration(exception);
            return ReadPhysical(_source, buffer[..count], offset);
        }
    }

    private async ValueTask<int> ReadAtCoreAsync(
        Memory<byte> buffer,
        long offset,
        CancellationToken cancellationToken)
    {
        if (buffer.IsEmpty || offset >= _logicalLength)
        {
            return 0;
        }

        int count = (int)Math.Min(buffer.Length, _logicalLength - offset);
        Stream stream = SelectReadStream(offset, out long available);
        count = (int)Math.Min(count, available);
        try
        {
            return await ReadPhysicalAsync(stream, buffer[..count], offset, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException &&
            ReferenceEquals(stream, _destination) &&
            State == MigrationState.Active)
        {
            FailMigration(exception);
            return await ReadPhysicalAsync(_source, buffer[..count], offset, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private Stream SelectReadStream(long offset, out long available)
    {
        if (State == MigrationState.Completed)
        {
            available = _logicalLength - offset;
            return _destination;
        }

        if (State == MigrationState.Active && offset < _migratedLength)
        {
            available = _migratedLength - offset;
            return _destination;
        }

        available = _logicalLength - offset;
        return _source;
    }

    private void WriteAtCore(ReadOnlySpan<byte> buffer, long offset)
    {
        long end = GetWriteEnd(offset, buffer.Length);
        MigrationState state = State;
        if (state == MigrationState.Completed)
        {
            WritePhysical(_destination, buffer, offset);
            _logicalLength = Math.Max(_logicalLength, end);
            return;
        }

        WritePhysical(_source, buffer, offset);
        _logicalLength = Math.Max(_logicalLength, end);
        if (state == MigrationState.Active)
        {
            try
            {
                WritePhysical(_destination, buffer, offset);
            }
            catch (Exception exception)
            {
                FailMigration(exception);
                throw;
            }
        }
    }

    private async ValueTask WriteAtCoreAsync(
        ReadOnlyMemory<byte> buffer,
        long offset,
        CancellationToken cancellationToken)
    {
        long end = GetWriteEnd(offset, buffer.Length);
        MigrationState state = State;
        if (state == MigrationState.Completed)
        {
            await WritePhysicalAsync(_destination, buffer, offset, cancellationToken)
                .ConfigureAwait(false);
            _logicalLength = Math.Max(_logicalLength, end);
            return;
        }

        await WritePhysicalAsync(_source, buffer, offset, cancellationToken).ConfigureAwait(false);
        _logicalLength = Math.Max(_logicalLength, end);
        if (state == MigrationState.Active)
        {
            try
            {
                await WritePhysicalAsync(_destination, buffer, offset, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                FailMigration(exception);
                throw;
            }
        }
    }

    private void SetLengthCore(long value)
    {
        MigrationState state = State;
        if (state == MigrationState.Completed)
        {
            _destination.SetLength(value);
        }
        else
        {
            _source.SetLength(value);
            _logicalLength = value;
            _migratedLength = Math.Min(_migratedLength, value);
            if (state == MigrationState.Active)
            {
                try
                {
                    _destination.SetLength(value);
                }
                catch (Exception exception)
                {
                    FailMigration(exception);
                    throw;
                }
            }
        }

        _logicalLength = value;
    }

    private void FlushCore()
    {
        MigrationState state = State;
        if (state == MigrationState.Completed)
        {
            _destination.Flush();
            return;
        }

        _source.Flush();
        if (state == MigrationState.Active)
        {
            try
            {
                _destination.Flush();
            }
            catch (Exception exception)
            {
                FailMigration(exception);
                throw;
            }
        }
    }

    private async ValueTask FlushCoreAsync(CancellationToken cancellationToken)
    {
        MigrationState state = State;
        if (state == MigrationState.Completed)
        {
            await _destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await _source.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (state == MigrationState.Active)
        {
            try
            {
                await _destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                FailMigration(exception);
                throw;
            }
        }
    }

    private void FailMigration(Exception exception)
    {
        if (Interlocked.CompareExchange(
                ref _state,
                (int)MigrationState.Failed,
                (int)MigrationState.Active) != (int)MigrationState.Active)
        {
            return;
        }

        _migrationCompletion.TrySetException(exception);
        _migrationCancellation.Cancel();
    }

    private void FailMigration(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(
                ref _state,
                (int)MigrationState.Failed,
                (int)MigrationState.Active) != (int)MigrationState.Active)
        {
            return;
        }

        _migrationCompletion.TrySetCanceled(cancellationToken);
    }

    private void EnterForeground()
    {
        ThrowIfDisposed();
        Interlocked.Increment(ref _foregroundWaiters);
        try
        {
            _operationGate.Wait();
        }
        finally
        {
            Interlocked.Decrement(ref _foregroundWaiters);
        }

        try
        {
            ThrowIfDisposed();
        }
        catch
        {
            ExitOperation();
            throw;
        }
    }

    private async ValueTask EnterForegroundAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        Interlocked.Increment(ref _foregroundWaiters);
        try
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _foregroundWaiters);
        }

        try
        {
            ThrowIfDisposed();
        }
        catch
        {
            ExitOperation();
            throw;
        }
    }

    private async ValueTask EnterBackgroundAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref _foregroundWaiters) == 0)
            {
                return;
            }

            _operationGate.Release();
            await Task.Yield();
        }
    }

    private void ExitOperation() => _operationGate.Release();

    private async Task<int> ReadArrayAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    private async Task WriteArrayAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        await WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    private int ReadPhysical(Stream stream, Span<byte> buffer, long offset)
    {
        ITeeRandomAccessStream? randomAccess = GetRandomAccess(stream);
        if (randomAccess?.CanReadAt == true)
        {
            return randomAccess.ReadAt(buffer, offset);
        }

        long position = stream.Position;
        try
        {
            stream.Position = offset;
            return stream.Read(buffer);
        }
        finally
        {
            stream.Position = position;
        }
    }

    private async ValueTask<int> ReadPhysicalAsync(
        Stream stream,
        Memory<byte> buffer,
        long offset,
        CancellationToken cancellationToken)
    {
        ITeeRandomAccessStream? randomAccess = GetRandomAccess(stream);
        if (randomAccess?.CanReadAt == true)
        {
            return await randomAccess.ReadAtAsync(buffer, offset, cancellationToken)
                .ConfigureAwait(false);
        }

        long position = stream.Position;
        try
        {
            stream.Position = offset;
            return await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            stream.Position = position;
        }
    }

    private void WritePhysical(Stream stream, ReadOnlySpan<byte> buffer, long offset)
    {
        ITeeRandomAccessStream? randomAccess = GetRandomAccess(stream);
        if (randomAccess?.CanWriteAt == true)
        {
            randomAccess.WriteAt(buffer, offset);
            return;
        }

        long position = stream.Position;
        try
        {
            stream.Position = offset;
            stream.Write(buffer);
        }
        finally
        {
            stream.Position = position;
        }
    }

    private async ValueTask WritePhysicalAsync(
        Stream stream,
        ReadOnlyMemory<byte> buffer,
        long offset,
        CancellationToken cancellationToken)
    {
        ITeeRandomAccessStream? randomAccess = GetRandomAccess(stream);
        if (randomAccess?.CanWriteAt == true)
        {
            await randomAccess.WriteAtAsync(buffer, offset, cancellationToken).ConfigureAwait(false);
            return;
        }

        long position = stream.Position;
        try
        {
            stream.Position = offset;
            await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            stream.Position = position;
        }
    }

    private async ValueTask<int> ReadExactlyAtAsync(
        Stream stream,
        Memory<byte> buffer,
        long offset,
        CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await ReadPhysicalAsync(
                    stream,
                    buffer[total..],
                    offset + total,
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private ITeeRandomAccessStream? GetRandomAccess(Stream stream) =>
        ReferenceEquals(stream, _source) ? _sourceRandomAccess : _destinationRandomAccess;

    private void DisposeStreams()
    {
        List<Exception>? failures = null;
        DisposeStream(_source, Volatile.Read(ref _leaveSourceOpen) != 0, ref failures);
        DisposeStream(_destination, Volatile.Read(ref _leaveDestinationOpen) != 0, ref failures);
        ThrowDisposeFailures(failures);
    }

    private async ValueTask DisposeStreamsAsync()
    {
        List<Exception>? failures = null;
        try
        {
            if (Volatile.Read(ref _leaveSourceOpen) != 0)
            {
                await _source.FlushAsync().ConfigureAwait(false);
            }
            else
            {
                await _source.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            if (Volatile.Read(ref _leaveDestinationOpen) != 0)
            {
                await _destination.FlushAsync().ConfigureAwait(false);
            }
            else
            {
                await _destination.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        ThrowDisposeFailures(failures);
    }

    private static void DisposeStream(
        Stream stream,
        bool leaveOpen,
        ref List<Exception>? failures)
    {
        try
        {
            if (leaveOpen)
            {
                stream.Flush();
            }
            else
            {
                stream.Dispose();
            }
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }
    }

    private static void ThrowDisposeFailures(List<Exception>? failures)
    {
        if (failures is { Count: 1 })
        {
            throw failures[0];
        }

        if (failures is { Count: > 1 })
        {
            throw new AggregateException(failures);
        }
    }

    private static long GetWriteEnd(long offset, int count)
    {
        try
        {
            return checked(offset + count);
        }
        catch (OverflowException exception)
        {
            throw new IOException("The write would exceed the maximum stream length.", exception);
        }
    }

    private static void ValidateStreams(Stream source, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (ReferenceEquals(source, destination))
        {
            throw new ArgumentException("Source and destination must be distinct streams.", nameof(destination));
        }

        if (!source.CanRead || !source.CanWrite || !source.CanSeek)
        {
            throw new ArgumentException("The source must be readable, writable, and seekable.", nameof(source));
        }

        if (!destination.CanRead || !destination.CanWrite || !destination.CanSeek)
        {
            throw new ArgumentException(
                "The destination must be readable, writable, and seekable.",
                nameof(destination));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    private enum MigrationState
    {
        Active,
        Completed,
        Failed,
    }
}
