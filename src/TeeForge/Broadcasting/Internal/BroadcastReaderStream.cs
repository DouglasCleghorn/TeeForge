using System.Buffers;
using System.IO.Pipelines;

namespace TeeForge.Broadcasting.Internal;

internal sealed class BroadcastReaderStream(BroadcastStream owner, PipeReader reader) : Stream
{
    private readonly Stream _stream = reader.AsStream();
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly object _disposeLock = new();
    private Task? _disposeTask;
    private long _position;
    private int _disposed;

    public override bool CanRead => Volatile.Read(ref _disposed) == 0;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return Interlocked.Read(ref _position);
        }
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        BeginRead();
        try
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            return FinishRead(_stream.Read(buffer));
        }
        finally
        {
            _operation.Release();
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override int ReadByte()
    {
        Span<byte> buffer = stackalloc byte[1];
        return Read(buffer) == 0 ? -1 : buffer[0];
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeginRead();
        try
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            return FinishRead(await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _operation.Release();
        }
    }

    public override void Flush() => ThrowIfDisposed();

    public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        ValidateCopyToArguments(destination, bufferSize);
        cancellationToken.ThrowIfCancellationRequested();
        BeginRead();
        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> remaining = result.Buffer;
                SequencePosition consumed = remaining.Start;
                try
                {
                    if (result.IsCanceled)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    foreach (ReadOnlyMemory<byte> segment in result.Buffer)
                    {
                        ReadOnlyMemory<byte> pending = segment;
                        while (!pending.IsEmpty)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            int count = Math.Min(bufferSize, pending.Length);
                            await destination.WriteAsync(pending[..count], cancellationToken).ConfigureAwait(false);
                            pending = pending[count..];
                            consumed = remaining.GetPosition(count);
                            remaining = remaining.Slice(count);
                            Interlocked.Add(ref _position, count);
                        }
                    }

                    if (result.IsCompleted)
                    {
                        owner.ThrowTerminalFailure();
                        return;
                    }
                }
                finally
                {
                    // A destination owns the shared memory until its awaited write has finished.
                    reader.AdvanceTo(consumed, consumed);
                }
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return Task.CompletedTask;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            BeginDispose().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await BeginDispose().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void BeginRead()
    {
        ThrowIfDisposed();
        if (!_operation.Wait(0))
        {
            throw new InvalidOperationException("Concurrent reads on one broadcast reader are not supported.");
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            _operation.Release();
            ThrowIfDisposed();
        }
    }

    private int FinishRead(int count)
    {
        if (count == 0)
        {
            owner.ThrowTerminalFailure();
        }

        Interlocked.Add(ref _position, count);
        return count;
    }

    private Task BeginDispose()
    {
        lock (_disposeLock)
        {
            return _disposeTask ??= DisposeCoreAsync();
        }
    }

    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref _disposed, 1);
        reader.CancelPendingRead();
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
            owner.ReaderClosed();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
