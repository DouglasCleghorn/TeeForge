namespace TeeForge.Tests;

public class RandomAccessMemoryStreamTests
{
    [Fact]
    public async Task Explicit_offset_operations_preserve_position()
    {
        await using var stream = new RandomAccessMemoryStream();
        await stream.WriteAsync(new byte[] { 0, 1, 2, 3, 4, 5 });
        stream.Position = 5;

        byte[] read = new byte[3];
        Assert.Equal(3, await stream.ReadAtAsync(read, 1));
        await stream.WriteAtAsync(new byte[] { 9, 8 }, 2);

        Assert.Equal([1, 2, 3], read);
        Assert.Equal(5, stream.Position);
        Assert.Equal([0, 1, 9, 8, 4, 5], stream.ToArray());
    }

    [Fact]
    public void Explicit_offset_write_expands_an_expandable_stream_and_zero_fills_gap()
    {
        using var stream = new RandomAccessMemoryStream();
        stream.WriteByte(1);
        stream.Position = 1;

        stream.WriteAt([2, 3], 4);

        Assert.Equal(1, stream.Position);
        Assert.Equal([1, 0, 0, 0, 2, 3], stream.ToArray());
    }

    [Fact]
    public async Task Read_only_buffer_rejects_random_writes()
    {
        await using var stream = new RandomAccessMemoryStream([1, 2, 3], writable: false);

        Assert.True(stream.CanReadAt);
        Assert.False(stream.CanWriteAt);
        Assert.Equal(2, stream.ReadAt(new byte[2], 1));
        Assert.Throws<NotSupportedException>(() => stream.WriteAt([4], 0));
        await Assert.ThrowsAsync<NotSupportedException>(
            async () => await stream.WriteAtAsync(new byte[] { 4 }, 0));
    }

    [Fact]
    public async Task Open_read_range_is_bounded_and_independent_of_position()
    {
        await using var stream = new RandomAccessMemoryStream([0, 1, 2, 3, 4, 5]);
        stream.Position = 4;

        await using Stream range = await stream.OpenReadRangeAsync(2, 3);
        byte[] bytes = new byte[5];
        int read = await range.ReadAsync(bytes);

        Assert.Equal(3, read);
        Assert.Equal([2, 3, 4], bytes[..read]);
        Assert.Equal(0, await range.ReadAsync(bytes));
        Assert.Equal(4, stream.Position);
    }

    [Fact]
    public async Task Concurrent_random_writes_do_not_interfere_or_move_position()
    {
        await using var stream = new RandomAccessMemoryStream(capacity: 1024);
        stream.SetLength(1024);
        stream.Position = 777;

        Task[] writes = Enumerable.Range(0, 64)
            .Select(index => Task.Run(async () =>
            {
                byte[] block = Enumerable.Repeat((byte)index, 16).ToArray();
                await stream.WriteAtAsync(block, index * block.Length);
            }))
            .ToArray();
        await Task.WhenAll(writes);

        Assert.Equal(777, stream.Position);
        byte[] contents = stream.ToArray();
        for (int index = 0; index < 64; index++)
        {
            Assert.All(contents.AsSpan(index * 16, 16).ToArray(), value => Assert.Equal((byte)index, value));
        }
    }

    [Fact]
    public async Task Copy_to_async_uses_a_stable_snapshot()
    {
        await using var stream = new RandomAccessMemoryStream([1, 2, 3, 4]);
        await using var destination = new MemoryStream();
        stream.Position = 1;

        await stream.CopyToAsync(destination, bufferSize: 2);

        Assert.Equal([2, 3, 4], destination.ToArray());
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public void Tee_random_access_discovers_memory_stream_capability()
    {
        using var stream = new RandomAccessMemoryStream();

        Assert.True(TeeRandomAccess.TryGet(stream, out ITeeRandomAccessStream? randomAccess));
        Assert.Same(stream, randomAccess);
    }
}
