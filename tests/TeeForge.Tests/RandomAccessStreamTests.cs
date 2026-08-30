namespace TeeForge.Tests;

public class RandomAccessStreamTests
{
    [Fact]
    public async Task File_adapter_reads_and_writes_without_moving_position()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [0, 1, 2, 3, 4, 5]);
            await using var file = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            file.Position = 4;

            Assert.True(TeeRandomAccess.TryGet(file, out ITeeRandomAccessStream? randomAccess));
            byte[] read = new byte[3];
            Assert.Equal(3, await randomAccess.ReadAtAsync(read, 1));
            await randomAccess.WriteAtAsync(new byte[] { 9, 8 }, 2);

            Assert.Equal([1, 2, 3], read);
            Assert.Equal(4, file.Position);
            byte[] complete = new byte[6];
            Assert.Equal(6, randomAccess.ReadAt(complete, 0));
            Assert.Equal([0, 1, 9, 8, 4, 5], complete);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Tee_random_access_compares_ranges_and_preserves_positions()
    {
        await using var primary = new TestRandomAccessStream([0, 1, 2, 3, 4, 5]);
        await using var mirror = new TestRandomAccessStream([0, 1, 2, 3, 4, 5]);
        primary.Position = 5;
        mirror.Position = 5;
        await using var tee = new TeeStream(
            new TeeStreamOptions(leaveOpen: true),
            primary,
            mirror);

        byte[] read = new byte[3];
        Assert.Equal(3, await tee.ReadAtAsync(read, 1));
        await tee.WriteAtAsync(new byte[] { 7, 8 }, 2);
        await using Stream range = await tee.OpenReadRangeAsync(1, 4);
        byte[] ranged = new byte[4];
        await range.ReadExactlyAsync(ranged);

        Assert.Equal([1, 2, 3], read);
        Assert.Equal([1, 7, 8, 4], ranged);
        Assert.Equal(5, primary.Position);
        Assert.Equal(5, mirror.Position);
        Assert.Equal(primary.ToArray(), mirror.ToArray());
    }

    [Fact]
    public void Tee_random_access_detects_mismatched_data()
    {
        using var primary = new TestRandomAccessStream([1, 2, 3]);
        using var mirror = new TestRandomAccessStream([1, 9, 3]);
        using var tee = new TeeStream(new TeeStreamOptions(leaveOpen: true), primary, mirror);

        TeeStreamConsistencyException exception = Assert.Throws<TeeStreamConsistencyException>(
            () => tee.ReadAt(new byte[3], 0));

        Assert.Equal("ReadAt", exception.OperationName);
        Assert.Equal(1, Assert.Single(exception.Mismatches).FirstDifferingByteOffset);
    }

    [Fact]
    public async Task Buffered_random_read_flushes_writes_without_moving_logical_position()
    {
        await using var primary = new TestRandomAccessStream(new byte[16]);
        await using var mirror = new TestRandomAccessStream(new byte[16]);
        await using var buffered = new TeeBufferedStream(
            [primary, mirror],
            new TeeBufferedStreamOptions(leaveOpen: true, bufferSize: 8));

        await buffered.WriteAsync(new byte[] { 1, 2, 3 });
        Assert.Equal(3, buffered.Position);
        Assert.Equal(0, primary.ToArray()[0]);

        byte[] read = new byte[3];
        Assert.Equal(3, await buffered.ReadAtAsync(read, 0));

        Assert.Equal([1, 2, 3], read);
        Assert.Equal(3, buffered.Position);
        Assert.Equal([1, 2, 3], primary.ToArray()[..3]);
        Assert.Equal(primary.ToArray(), mirror.ToArray());
    }

    [Fact]
    public async Task Dynamic_allocation_translates_logical_offsets_to_upstream_random_access()
    {
        await using var backing = new TestRandomAccessStream([]);
        await using DynamicAllocationStream sparse = DynamicAllocationStream.Create(
            backing,
            virtualCapacity: 16L * 64 * 1024,
            blockSize: 64 * 1024,
            new DynamicAllocationStreamOptions(
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
