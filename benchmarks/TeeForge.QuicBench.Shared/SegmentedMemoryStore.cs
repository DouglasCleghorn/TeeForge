using TeeForge.RandomAccess;

namespace TeeForge.QuicBench;

/// <summary>
/// A benchmark-only, long-addressable memory store composed of MemoryStream segments.
/// System.IO.MemoryStream itself has an Int32-sized capacity and cannot hold 2+ GiB.
/// </summary>
internal sealed class SegmentedMemoryStore : ITeeRandomAccessStream, IDisposable
{
    internal const int DefaultSegmentSize = 64 * 1024 * 1024;

    private readonly object[] _segmentLocks;
    private readonly MemoryStream[] _segments;
    private bool _disposed;

    internal SegmentedMemoryStore(
        long length,
        int segmentSize = DefaultSegmentSize,
        bool initialize = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segmentSize);
        Length = length;
        SegmentSize = segmentSize;
        int segmentCount = checked((int)((length + segmentSize - 1) / segmentSize));
        _segments = new MemoryStream[segmentCount];
        _segmentLocks = new object[segmentCount];

        for (int index = 0; index < segmentCount; index++)
        {
            int currentLength = checked((int)Math.Min(segmentSize, length - ((long)index * segmentSize)));
            byte[] bytes = GC.AllocateUninitializedArray<byte>(currentLength);
            if (initialize)
            {
                FillDeterministic(bytes, index);
            }
            else
            {
                Array.Clear(bytes);
            }

            _segments[index] = new MemoryStream(
                bytes,
                index: 0,
                count: bytes.Length,
                writable: true,
                publiclyVisible: true);
            _segmentLocks[index] = new object();
        }
    }

    public bool CanReadAt => !_disposed;

    public bool CanWriteAt => !_disposed;

    internal long Length { get; }

    internal int SegmentSize { get; }

    internal Stream OpenStream() =>
        !_disposed
            ? new SegmentedMemoryStream(this)
            : throw new ObjectDisposedException(nameof(SegmentedMemoryStore));

    public int ReadAt(Span<byte> buffer, long offset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= Length || buffer.IsEmpty)
        {
            return 0;
        }

        int total = checked((int)Math.Min(buffer.Length, Length - offset));
        int copied = 0;
        while (copied < total)
        {
            int segmentIndex = checked((int)(offset / SegmentSize));
            int segmentOffset = checked((int)(offset % SegmentSize));
            int count = Math.Min(total - copied, checked((int)(_segments[segmentIndex].Length - segmentOffset)));
            lock (_segmentLocks[segmentIndex])
            {
                _segments[segmentIndex]
                    .GetBuffer()
                    .AsSpan(segmentOffset, count)
                    .CopyTo(buffer[copied..]);
            }

            offset += count;
            copied += count;
        }

        return total;
    }

    public ValueTask<int> ReadAtAsync(
        Memory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ReadAt(buffer.Span, offset));
    }

    public void WriteAt(ReadOnlySpan<byte> buffer, long offset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset > Length || buffer.Length > Length - offset)
        {
            throw new ArgumentException("The write extends beyond the fixed-size memory store.", nameof(buffer));
        }

        int copied = 0;
        while (copied < buffer.Length)
        {
            int segmentIndex = checked((int)(offset / SegmentSize));
            int segmentOffset = checked((int)(offset % SegmentSize));
            int count = Math.Min(
                buffer.Length - copied,
                checked((int)(_segments[segmentIndex].Length - segmentOffset)));
            lock (_segmentLocks[segmentIndex])
            {
                buffer.Slice(copied, count).CopyTo(
                    _segments[segmentIndex].GetBuffer().AsSpan(segmentOffset, count));
            }

            offset += count;
            copied += count;
        }
    }

    public ValueTask WriteAtAsync(
        ReadOnlyMemory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteAt(buffer.Span, offset);
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (MemoryStream segment in _segments)
        {
            segment.Dispose();
        }
    }

    private static void FillDeterministic(byte[] buffer, int segmentIndex)
    {
        var random = new Random(unchecked(0x4D454D31 + segmentIndex));
        random.NextBytes(buffer);
    }

    private sealed class SegmentedMemoryStream : Stream
    {
        private readonly object _positionLock = new();
        private readonly SegmentedMemoryStore _store;
        private long _position;

        internal SegmentedMemoryStream(SegmentedMemoryStore store)
        {
            _store = store;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override long Length => _store.Length;

        public override long Position
        {
            get
            {
                lock (_positionLock)
                {
                    return _position;
                }
            }

            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(value, Length);

                lock (_positionLock)
                {
                    _position = value;
                }
            }
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            lock (_positionLock)
            {
                int count = _store.ReadAt(buffer, _position);
                _position += count;
                return count;
            }
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (_positionLock)
            {
                long position = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => checked(_position + offset),
                    SeekOrigin.End => checked(Length + offset),
                    _ => throw new ArgumentOutOfRangeException(nameof(origin)),
                };
                if (position < 0 || position > Length)
                {
                    throw new IOException("The requested position is outside the fixed-size memory store.");
                }

                _position = position;
                return position;
            }
        }

        public override void SetLength(long value) =>
            throw new NotSupportedException("The segmented benchmark memory store has a fixed length.");

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            lock (_positionLock)
            {
                _store.WriteAt(buffer, _position);
                _position += buffer.Length;
            }
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
    }
}
