using System.Runtime.ExceptionServices;
using TeeForge.RandomAccess;

namespace TeeForge.Composition;

/// <summary>
/// Provides a stable stream endpoint that can hand operations off to a replacement stream.
/// </summary>
/// <remarks>
/// All operations are serialized. A handoff waits for the active operation, if any, and future
/// operations use the supplied replacement stream. The outgoing stream is flushed before the
/// replacement becomes active so writes reach their shared final destination in order.
/// </remarks>
public class HandoffStream : Stream, ITeeRandomAccessStream
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly bool _leaveOpen;
    private Stream? _stream;
    private PendingMigration? _pendingMigration;
    private int _disposed;

    /// <summary>Initializes a stable endpoint over the supplied stream.</summary>
    /// <param name="stream">The initial stream pipeline.</param>
    /// <param name="leaveOpen">
    /// Whether disposing this endpoint leaves the current stream pipeline open.
    /// </param>
    public HandoffStream(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    /// <summary>Gets whether disposing this endpoint leaves the current stream pipeline open.</summary>
    public bool LeaveOpen => _leaveOpen;

    /// <inheritdoc/>
    public override bool CanRead => !IsDisposed && Volatile.Read(ref _stream)?.CanRead == true;

    /// <inheritdoc/>
    public override bool CanSeek => !IsDisposed && Volatile.Read(ref _stream)?.CanSeek == true;

    /// <inheritdoc/>
    public override bool CanTimeout => !IsDisposed && Volatile.Read(ref _stream)?.CanTimeout == true;

    /// <inheritdoc/>
    public override bool CanWrite => !IsDisposed && Volatile.Read(ref _stream)?.CanWrite == true;

    /// <inheritdoc/>
    public bool CanReadAt
    {
        get
        {
            Stream? stream = Volatile.Read(ref _stream);
            return !IsDisposed && stream is not null && SupportsReadAt(stream);
        }
    }

    /// <inheritdoc/>
    public bool CanWriteAt
    {
        get
        {
            Stream? stream = Volatile.Read(ref _stream);
            return !IsDisposed && stream is not null && SupportsWriteAt(stream);
        }
    }

    /// <inheritdoc/>
    public override long Length
    {
        get
        {
            EnterOperation();
            try
            {
                return CurrentStream.Length;
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
            EnterOperation();
            try
            {
                return CurrentStream.Position;
            }
            finally
            {
                ExitOperation();
            }
        }
        set
        {
            EnterOperation();
            try
            {
                CurrentStream.Position = value;
            }
            finally
            {
                ExitOperation();
            }
        }
    }

    /// <inheritdoc/>
    public int ReadAt(Span<byte> buffer, long offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        EnterOperation();
        try
        {
            Stream stream = CurrentStream;
            if (TeeRandomAccess.TryGet(stream, out ITeeRandomAccessStream? randomAccess) &&
                randomAccess.CanReadAt)
            {
                return randomAccess.ReadAt(buffer, offset);
            }

            EnsureFallbackReadAt(stream);
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
        await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Stream stream = CurrentStream;
            if (TeeRandomAccess.TryGet(stream, out ITeeRandomAccessStream? randomAccess) &&
                randomAccess.CanReadAt)
            {
                return await randomAccess.ReadAtAsync(buffer, offset, cancellationToken)
                    .ConfigureAwait(false);
            }

            EnsureFallbackReadAt(stream);
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
        finally
        {
            ExitOperation();
        }
    }

    /// <inheritdoc/>
    public void WriteAt(ReadOnlySpan<byte> buffer, long offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        EnterOperation();
        try
        {
            Stream stream = CurrentStream;
            if (TeeRandomAccess.TryGet(stream, out ITeeRandomAccessStream? randomAccess) &&
                randomAccess.CanWriteAt)
            {
                randomAccess.WriteAt(buffer, offset);
                return;
            }

            EnsureFallbackWriteAt(stream);
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
        await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Stream stream = CurrentStream;
            if (TeeRandomAccess.TryGet(stream, out ITeeRandomAccessStream? randomAccess) &&
                randomAccess.CanWriteAt)
            {
                await randomAccess.WriteAtAsync(buffer, offset, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            EnsureFallbackWriteAt(stream);
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
        finally
        {
            ExitOperation();
        }
    }

    /// <inheritdoc/>
    public override int ReadTimeout
    {
        get
        {
            EnterOperation();
            try
            {
                return CurrentStream.ReadTimeout;
            }
            finally
            {
                ExitOperation();
            }
        }
        set
        {
            EnterOperation();
            try
            {
                CurrentStream.ReadTimeout = value;
            }
            finally
            {
                ExitOperation();
            }
        }
    }

    /// <inheritdoc/>
    public override int WriteTimeout
    {
        get
        {
            EnterOperation();
            try
            {
                return CurrentStream.WriteTimeout;
            }
            finally
            {
                ExitOperation();
            }
        }
        set
        {
            EnterOperation();
            try
            {
                CurrentStream.WriteTimeout = value;
            }
            finally
            {
                ExitOperation();
            }
        }
    }

    /// <summary>
    /// Flushes the outgoing stream and atomically hands future operations off to
    /// <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">
    /// The replacement stream, which is assumed to have the same final destination as the
    /// outgoing stream.
    /// </param>
    /// <remarks>
    /// The outgoing stream is not disposed. The replacement or caller retains responsibility for
    /// it. If flushing fails, the outgoing stream remains active.
    /// </remarks>
    public void Handoff(Stream stream)
    {
        ValidateReplacement(stream);
        EnterOperation();
        try
        {
            CurrentStream.Flush();
            _stream = stream;
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>
    /// Asynchronously waits for exclusive access, flushes the outgoing stream, and hands future
    /// operations off to <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">
    /// The replacement stream, which is assumed to have the same final destination as the
    /// outgoing stream.
    /// </param>
    /// <param name="cancellationToken">The token that cancels waiting for the handoff boundary.</param>
    /// <remarks>
    /// The outgoing stream is not disposed. The replacement or caller retains responsibility for
    /// it. If waiting or flushing fails, the outgoing stream remains active.
    /// </remarks>
    public async ValueTask HandoffAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ValidateReplacement(stream);
        await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CurrentStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            _stream = stream;
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>
    /// Migrates the current stream to <paramref name="destination"/> while this stable endpoint
    /// remains readable and writable.
    /// </summary>
    /// <param name="destination">The readable, writable, seekable replacement backing stream.</param>
    /// <param name="options">Migration, retired-source, and failure-destination ownership options.</param>
    /// <param name="cancellationToken">The token that cancels migration.</param>
    /// <remarks>
    /// The current stream is flushed and atomically replaced by a <see cref="MigratingStream"/>
    /// before background copying starts. Successful migration atomically replaces that wrapper
    /// with <paramref name="destination"/>. Failure or cancellation restores the original source,
    /// except that a failure limited to optional source cleanup keeps the already-authoritative
    /// destination. Destination ownership transfers to this HandoffStream after a successful
    /// switch, so <see cref="LeaveOpen"/> controls its eventual disposal. If another handoff
    /// replaces the migration wrapper first, this method does not overwrite that replacement.
    /// If the authoritative backing rejects the live cursor, the backing remains installed and
    /// migration reports the failure. An earlier copy or cancellation error takes precedence.
    /// </remarks>
    public async Task MigrateAsync(
        Stream destination,
        MigratingStreamOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateReplacement(destination);
        MigratingStreamOptions resolvedOptions = options ?? new MigratingStreamOptions(
            leaveSourceOpen: _leaveOpen);
        Task completion;

        await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Stream source = CurrentStream;
            if (ReferenceEquals(_pendingMigration?.Wrapper, source))
            {
                throw new InvalidOperationException("A migration is already running on this endpoint.");
            }

            await source.FlushAsync(cancellationToken).ConfigureAwait(false);
            var migration = new MigratingStream(
                source,
                destination,
                resolvedOptions,
                startMigration: false,
                cancellationToken: cancellationToken);
            _stream = migration;
            try
            {
                migration.StartMigration();
            }
            catch
            {
                migration.ReleaseSourceOwnership();
                _stream = source;
                migration.Dispose();
                throw;
            }

            completion = FinishMigrationHandoffAsync(migration, source, destination);
            _pendingMigration = new PendingMigration(migration, completion);
        }
        finally
        {
            ExitOperation();
        }

        await completion.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override void Flush()
    {
        EnterOperation();
        try
        {
            CurrentStream.Flush();
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <inheritdoc/>
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CurrentStream.FlushAsync(cancellationToken).ConfigureAwait(false);
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
        EnterOperation();
        try
        {
            return CurrentStream.Read(buffer, offset, count);
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        EnterOperation();
        try
        {
            return CurrentStream.Read(buffer);
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <inheritdoc/>
    public override int ReadByte()
    {
        EnterOperation();
        try
        {
            return CurrentStream.ReadByte();
        }
        finally
        {
            ExitOperation();
        }
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
        await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CurrentStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
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
        EnterOperation();
        try
        {
            CurrentStream.Write(buffer, offset, count);
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnterOperation();
        try
        {
            CurrentStream.Write(buffer);
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <inheritdoc/>
    public override void WriteByte(byte value)
    {
        EnterOperation();
        try
        {
            CurrentStream.WriteByte(value);
        }
        finally
        {
            ExitOperation();
        }
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
        await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CurrentStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        EnterOperation();
        try
        {
            return CurrentStream.Seek(offset, origin);
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <inheritdoc/>
    public override void SetLength(long value)
    {
        EnterOperation();
        try
        {
            CurrentStream.SetLength(value);
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
        EnterOperation();
        try
        {
            CurrentStream.CopyTo(destination, bufferSize);
        }
        finally
        {
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
        await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CurrentStream.CopyToAsync(destination, bufferSize, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                Stream? stream = DetachForDisposalAsync().AsTask().GetAwaiter().GetResult();
                if (_leaveOpen)
                {
                    stream?.Flush();
                }
                else
                {
                    stream?.Dispose();
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
            return;
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                Stream? stream = await DetachForDisposalAsync().ConfigureAwait(false);
                if (stream is not null)
                {
                    if (_leaveOpen)
                    {
                        await stream.FlushAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        await stream.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                await base.DisposeAsync().ConfigureAwait(false);
                GC.SuppressFinalize(this);
            }
            return;
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask<Stream?> DetachForDisposalAsync()
    {
        PendingMigration? pending;
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            pending = _pendingMigration;
            if (pending is null || !ReferenceEquals(_stream, pending.Wrapper))
            {
                Stream? current = _stream;
                _stream = null;
                return current;
            }

            pending.Wrapper.CancelMigration();
        }
        finally
        {
            ExitOperation();
        }

        // The final handoff owns the wrapper and installs the authoritative backing even on failure.
        // Its caller observes any migration error. Dispose applies this endpoint's ownership policy.
        await pending.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return Interlocked.Exchange(ref _stream, null);
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private Stream CurrentStream => _stream!;

    private void EnterOperation()
    {
        ThrowIfDisposed();
        _operationGate.Wait();
        try
        {
            ThrowIfDisposed();
        }
        catch
        {
            _operationGate.Release();
            throw;
        }
    }

    private async ValueTask EnterOperationAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
        }
        catch
        {
            _operationGate.Release();
            throw;
        }
    }

    private void ExitOperation() => _operationGate.Release();

    private async Task FinishMigrationHandoffAsync(MigratingStream migration, Stream source, Stream destination)
    {
        ExceptionDispatchInfo? failure = null;
        try
        {
            await migration.MigrationCompletion.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = ExceptionDispatchInfo.Capture(error);
        }

        // An endpoint being disposed still needs its final backing installed.
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_stream, migration))
            {
                bool useDestination = migration.DestinationIsAuthoritative;
                Stream replacement = useDestination ? destination : source;
                if (useDestination)
                {
                    migration.ReleaseDestinationOwnership();
                }
                else
                {
                    migration.ReleaseSourceOwnership();
                }

                // No foreground operation can observe the replacement until the gate is released.
                // A flush or cursor error must leave it usable, even if the source was truncated.
                _stream = replacement;
                replacement.Position = migration.Position;
                await migration.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                failure = ExceptionDispatchInfo.Capture(new InvalidOperationException(
                    "The migration wrapper was replaced before migration finished.", failure?.SourceException));
            }
        }
        catch (Exception error)
        {
            failure ??= ExceptionDispatchInfo.Capture(error);
        }
        finally
        {
            try
            {
                await migration.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                failure ??= ExceptionDispatchInfo.Capture(error);
            }
            finally
            {
                if (ReferenceEquals(_pendingMigration?.Wrapper, migration))
                {
                    _pendingMigration = null;
                }
                ExitOperation();
            }
        }

        failure?.Throw();
    }

    private sealed record PendingMigration(MigratingStream Wrapper, Task Completion);

    private void ValidateReplacement(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (ReferenceEquals(stream, this))
        {
            throw new InvalidOperationException("A HandoffStream cannot hand off to itself.");
        }
    }

    private static bool SupportsReadAt(Stream stream) =>
        (TeeRandomAccess.TryGet(stream, out ITeeRandomAccessStream? randomAccess) &&
            randomAccess.CanReadAt) ||
        (stream.CanSeek && stream.CanRead);

    private static bool SupportsWriteAt(Stream stream) =>
        (TeeRandomAccess.TryGet(stream, out ITeeRandomAccessStream? randomAccess) &&
            randomAccess.CanWriteAt) ||
        (stream.CanSeek && stream.CanWrite);

    private static void EnsureFallbackReadAt(Stream stream)
    {
        if (!stream.CanSeek || !stream.CanRead)
        {
            throw new NotSupportedException("The current stream does not support random-access reads.");
        }
    }

    private static void EnsureFallbackWriteAt(Stream stream)
    {
        if (!stream.CanSeek || !stream.CanWrite)
        {
            throw new NotSupportedException("The current stream does not support random-access writes.");
        }
    }

    private async Task<int> ReadArrayAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CurrentStream.ReadAsync(
                    buffer.AsMemory(offset, count),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ExitOperation();
        }
    }

    private async Task WriteArrayAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CurrentStream.WriteAsync(
                    buffer.AsMemory(offset, count),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ExitOperation();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
}
