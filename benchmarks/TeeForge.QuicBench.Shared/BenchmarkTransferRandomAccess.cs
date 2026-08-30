using TeeForge.RandomAccess;

namespace TeeForge.QuicBench;

/// <summary>
/// A transfer-only endpoint that generates reads and discards writes without a backing store.
/// </summary>
internal sealed class BenchmarkTransferRandomAccess : ITeeRandomAccessStream
{
    private readonly byte[] _pattern = new byte[1024 * 1024];
    private long _writeChecksum;

    internal BenchmarkTransferRandomAccess(long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        Length = length;
        new Random(0x51554943).NextBytes(_pattern);
    }

    public bool CanReadAt => true;

    public bool CanWriteAt => true;

    internal long Length { get; }

    public int ReadAt(Span<byte> buffer, long offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= Length || buffer.IsEmpty)
        {
            return 0;
        }

        int total = checked((int)Math.Min(buffer.Length, Length - offset));
        int copied = 0;
        int patternOffset = checked((int)(offset % _pattern.Length));
        while (copied < total)
        {
            int count = Math.Min(total - copied, _pattern.Length - patternOffset);
            _pattern.AsSpan(patternOffset, count).CopyTo(buffer[copied..]);
            copied += count;
            patternOffset = 0;
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
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset > Length || buffer.Length > Length - offset)
        {
            throw new ArgumentException("The write extends beyond the synthetic endpoint.", nameof(buffer));
        }

        if (!buffer.IsEmpty)
        {
            Interlocked.Add(ref _writeChecksum, buffer[0]);
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
}
