namespace TeeForge.Experimental.Storage.Tests;

public class StorageRandomAccessTests
{
    [Fact]
    public async Task Dynamic_allocation_translates_logical_offsets_to_upstream_random_access()
    {
        await using var backing = new TestRandomAccessStream([]);
        await using SparseDiskImage sparse = SparseDiskImage.Create(
            backing,
            virtualCapacity: 16L * 64 * 1024,
            blockSize: 64 * 1024,
            new SparseDiskImageOptions(
                leaveOpen: true,
                freeBlockQueueCapacity: 0,
                freeBlockQueueLowWatermark: 0));
        backing.ResetRandomAccessCounts();
        sparse.Position = 123;

        await sparse.WriteAtAsync(new byte[] { 4, 5, 6, 7 }, 70_000);
        byte[] read = new byte[4];
        Assert.Equal(4, await sparse.ReadAtAsync(read, 70_000));

        Assert.Equal([4, 5, 6, 7], read);
        Assert.Equal(123, sparse.Position);
        Assert.True(backing.RandomWriteCalls > 0);
        Assert.True(backing.RandomReadCalls > 0);
    }

    private sealed class TestRandomAccessStream : MemoryStream, ITeeRandomAccessStream
    {
        private readonly object _gate = new();

        internal TestRandomAccessStream(byte[] data)
            : base()
        {
            Write(data);
            Position = 0;
        }

        internal int RandomReadCalls { get; private set; }

        internal int RandomWriteCalls { get; private set; }

        public bool CanReadAt => CanRead;

        public bool CanWriteAt => CanWrite;

        public int ReadAt(Span<byte> buffer, long offset)
        {
            lock (_gate)
            {
                RandomReadCalls++;
                long saved = Position;
                Position = offset;
                try
                {
                    return Read(buffer);
                }
                finally
                {
                    Position = saved;
                }
            }
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
            lock (_gate)
            {
                RandomWriteCalls++;
                long saved = Position;
                Position = offset;
                try
                {
                    Write(buffer);
                }
                finally
                {
                    Position = saved;
                }
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

        internal void ResetRandomAccessCounts()
        {
            RandomReadCalls = 0;
            RandomWriteCalls = 0;
        }
    }
}
