using TeeForge.RandomAccess.Internal;

namespace TeeForge.RandomAccess;

/// <summary>
/// Provides an in-memory stream with thread-safe, position-independent reads and writes.
/// </summary>
/// <remarks>
/// Explicit-offset operations are serialized with ordinary stream operations so they never
/// observe or change <see cref="Stream.Position"/>. The asynchronous operations complete
/// synchronously because all data is held in memory.
/// </remarks>
public class RandomAccessMemoryStream : MemoryStream, ITeeRandomAccessStream, ITeeRangeReadSource
{
    private readonly object _gate = new();

    /// <summary>Initializes an empty, expandable stream.</summary>
    public RandomAccessMemoryStream()
    {
    }

    /// <summary>Initializes an empty, expandable stream with the specified capacity.</summary>
    public RandomAccessMemoryStream(int capacity)
        : base(capacity)
    {
    }

    /// <summary>Initializes a non-expandable, writable stream over the specified buffer.</summary>
    public RandomAccessMemoryStream(byte[] buffer)
        : base(buffer)
    {
    }

    /// <summary>Initializes a non-expandable stream over the specified buffer.</summary>
    public RandomAccessMemoryStream(byte[] buffer, bool writable)
        : base(buffer, writable)
    {
    }

    /// <summary>Initializes a non-expandable, writable stream over a region of a buffer.</summary>
    public RandomAccessMemoryStream(byte[] buffer, int index, int count)
        : base(buffer, index, count)
    {
    }

    /// <summary>Initializes a non-expandable stream over a region of a buffer.</summary>
    public RandomAccessMemoryStream(byte[] buffer, int index, int count, bool writable)
        : base(buffer, index, count, writable)
    {
    }

    /// <summary>Initializes a non-expandable stream over a region of a buffer.</summary>
    public RandomAccessMemoryStream(
        byte[] buffer,
        int index,
        int count,
        bool writable,
        bool publiclyVisible)
        : base(buffer, index, count, writable, publiclyVisible)
    {
    }

    /// <inheritdoc />
    public bool CanReadAt => CanRead;

    /// <inheritdoc />
    public bool CanWriteAt => CanWrite;

    /// <inheritdoc />
    public int ReadAt(Span<byte> buffer, long offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        lock (_gate)
        {
            long savedPosition = base.Position;
            try
            {
                base.Position = offset;
                return base.Read(buffer);
            }
            finally
            {
                base.Position = savedPosition;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<int> ReadAtAsync(
        Memory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ReadAt(buffer.Span, offset));
    }

    /// <inheritdoc />
    public void WriteAt(ReadOnlySpan<byte> buffer, long offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        lock (_gate)
        {
            long savedPosition = base.Position;
            try
            {
                base.Position = offset;
                base.Write(buffer);
            }
            finally
            {
                base.Position = savedPosition;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask WriteAtAsync(
        ReadOnlyMemory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteAt(buffer.Span, offset);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenReadRangeAsync(
        long offset,
        long length,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            EnsureReadable();
            long sourceLength = base.Length;
            long boundedLength = offset >= sourceLength
                ? 0
                : Math.Min(length, sourceLength - offset);

            return ValueTask.FromResult<Stream>(
                new BoundedRandomAccessReadStream(this, offset, boundedLength));
        }
    }

    /// <inheritdoc />
    public override int Capacity
    {
        get
        {
            lock (_gate)
            {
                return base.Capacity;
            }
        }
        set
        {
            lock (_gate)
            {
                base.Capacity = value;
            }
        }
    }

    /// <inheritdoc />
    public override long Length
    {
        get
        {
            lock (_gate)
            {
                return base.Length;
            }
        }
    }

    /// <inheritdoc />
    public override long Position
    {
        get
        {
            lock (_gate)
            {
                return base.Position;
            }
        }
        set
        {
            lock (_gate)
            {
                base.Position = value;
            }
        }
    }

    /// <inheritdoc />
    public override void Flush()
    {
        lock (_gate)
        {
            base.Flush();
        }
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return base.FlushAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public override void CopyTo(Stream destination, int bufferSize)
    {
        lock (_gate)
        {
            base.CopyTo(destination, bufferSize);
        }
    }

    /// <inheritdoc />
    public override Task CopyToAsync(
        Stream destination,
        int bufferSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);

        byte[] remaining;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = checked((int)Math.Max(0, base.Length - base.Position));
            remaining = new byte[count];
            _ = base.Read(remaining);
        }

        return destination.WriteAsync(remaining, cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            return base.Read(buffer, offset, count);
        }
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        lock (_gate)
        {
            return base.Read(buffer);
        }
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return base.ReadAsync(buffer, offset, count, cancellationToken);
        }
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    /// <inheritdoc />
    public override int ReadByte()
    {
        lock (_gate)
        {
            return base.ReadByte();
        }
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin loc)
    {
        lock (_gate)
        {
            return base.Seek(offset, loc);
        }
    }

    /// <inheritdoc />
    public override void SetLength(long value)
    {
        lock (_gate)
        {
            base.SetLength(value);
        }
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            base.Write(buffer, offset, count);
        }
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        lock (_gate)
        {
            base.Write(buffer);
        }
    }

    /// <inheritdoc />
    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }
    }

    /// <inheritdoc />
    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    /// <inheritdoc />
    public override void WriteByte(byte value)
    {
        lock (_gate)
        {
            base.WriteByte(value);
        }
    }

    /// <inheritdoc />
    public override byte[] ToArray()
    {
        lock (_gate)
        {
            return base.ToArray();
        }
    }

    /// <inheritdoc />
    public override byte[] GetBuffer()
    {
        lock (_gate)
        {
            return base.GetBuffer();
        }
    }

    /// <inheritdoc />
    public override bool TryGetBuffer(out ArraySegment<byte> buffer)
    {
        lock (_gate)
        {
            return base.TryGetBuffer(out buffer);
        }
    }

    /// <inheritdoc />
    public override void WriteTo(Stream stream)
    {
        lock (_gate)
        {
            base.WriteTo(stream);
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        lock (_gate)
        {
            base.Dispose(disposing);
        }
    }

    private void EnsureReadable()
    {
        if (!base.CanRead)
        {
            throw new NotSupportedException("The stream does not support random-access reads.");
        }
    }
}
