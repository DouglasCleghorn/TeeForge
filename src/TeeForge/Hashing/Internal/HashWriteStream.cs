namespace TeeForge.Hashing.Internal;

internal sealed class HashWriteStream : Stream
{
    private readonly HashCompletionCoordinator? _completion;
    private readonly IHashAccumulator _hash;
    private readonly int _resultIndex;
    private int _disposed;

    internal HashWriteStream(TeeHashAlgorithmId algorithm) => _hash = TeeHashAlgorithmFactory.Create(algorithm);

    internal HashWriteStream(
        TeeHashAlgorithmId algorithm,
        HashCompletionCoordinator completion,
        int resultIndex)
        : this(algorithm)
    {
        _completion = completion;
        _resultIndex = resultIndex;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => Volatile.Read(ref _disposed) == 0;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    internal byte[] GetHashAndReset()
    {
        ThrowIfDisposed();
        return _hash.GetHashAndReset();
    }

    public override void Flush() => ThrowIfDisposed();

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        ThrowIfDisposed();
        return Task.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(Span<byte> buffer) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfDisposed();
        _hash.Append(buffer);
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override void WriteByte(byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        buffer[0] = value;
        Write(buffer);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                if (_completion is not null)
                {
                    _completion.Complete(_resultIndex, _hash.GetHashAndReset());
                }
            }
            finally
            {
                _hash.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    internal void DisposeWithoutFinalizing()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _hash.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
