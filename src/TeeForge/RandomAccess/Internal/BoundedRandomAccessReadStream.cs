namespace TeeForge.RandomAccess.Internal;

internal sealed class BoundedRandomAccessReadStream : Stream
{
    private readonly ITeeRandomAccessStream _source;
    private readonly long _start;
    private readonly long _length;
    private long _position;
    private int _disposed;

    internal BoundedRandomAccessReadStream(
        ITeeRandomAccessStream source,
        long start,
        long length)
    {
        _source = source;
        _start = start;
        _length = length;
    }

    public override bool CanRead => !IsDisposed && _source.CanReadAt;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            return _length;
        }
    }

    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return _position;
        }
        set => throw new NotSupportedException("Range streams do not support seeking.");
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        int count = GetReadCount(buffer.Length);
        if (count == 0)
        {
            return 0;
        }

        int read = _source.ReadAt(buffer[..count], checked(_start + _position));
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        int count = GetReadCount(buffer.Length);
        if (count == 0)
        {
            return 0;
        }

        int read = await _source.ReadAtAsync(
            buffer[..count],
            checked(_start + _position),
            cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override void Flush()
    {
        ThrowIfDisposed();
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("Range streams do not support seeking.");

    public override void SetLength(long value) =>
        throw new NotSupportedException("Range streams are read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Range streams are read-only.");

    protected override void Dispose(bool disposing)
    {
        Volatile.Write(ref _disposed, 1);
        base.Dispose(disposing);
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private int GetReadCount(int requested) =>
        (int)Math.Min(requested, _length - _position);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(IsDisposed, this);
}
