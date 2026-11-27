namespace TeeForge.Tests;

public class ReadAheadStreamTests
{
    [Fact]
    public async Task Seeks_reuse_buffered_bytes_and_positional_reads_preserve_both_cursors()
    {
        byte[] data = Enumerable.Range(0, 20).Select(value => (byte)value).ToArray();
        using var source = new RangeSource(data, (offset, length) => new ShortRange(data, offset, length));
        source.Position = 3;
        await using var reader = new ReadAheadStream(source, windowSize: 6, leaveOpen: true);
        byte[] buffer = new byte[5];
        await reader.ReadExactlyAsync(buffer);
        Assert.Equal(data[3..8], buffer);
        Assert.Equal(3, source.Position);
        Assert.Equal(8, reader.Position);

        reader.Seek(-4, SeekOrigin.Current);
        Assert.Equal(4, reader.ReadByte());
        Assert.Equal(2, await reader.ReadAtAsync(buffer.AsMemory(0, 2), 6));
        Assert.Equal(data[6..8], buffer[..2]);
        Assert.Single(source.Ranges);
        Assert.Equal(5, reader.Position);

        Assert.Equal(2, reader.ReadAt(buffer, 16));
        Assert.Equal(data[16..18], buffer[..2]);
        Assert.Equal(5, reader.Position);
        Assert.Equal(2, source.Ranges.Count);
        Assert.False(source.Ranges[0].CanRead);
        reader.Seek(3, SeekOrigin.End);
        Assert.False(source.Ranges[1].CanRead);
        Assert.Equal(-1, reader.ReadByte());
        Assert.Equal(3, source.Position);
        Assert.False(reader.CanWrite);
        Assert.Throws<NotSupportedException>(() => reader.WriteAt([0], 0));
    }

    [Fact]
    public async Task An_incomplete_range_is_closed_and_the_same_position_can_be_retried()
    {
        byte[] bytes = [4, 5, 6];
        int attempt = 0;
        using var source = new RangeSource(bytes, (offset, length) =>
            ++attempt == 1 ? new MemoryStream() : new MemoryStream(bytes, offset, length, writable: false));
        await using var reader = new ReadAheadStream(source, leaveOpen: true);
        await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadAsync(new byte[3]).AsTask());
        Assert.Equal(0, reader.Position);
        Assert.False(Assert.Single(source.Ranges).CanRead);
        byte[] result = new byte[3];
        await reader.ReadExactlyAsync(result);
        Assert.Equal(bytes, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_and_disposal_close_a_pending_range(bool disposeReader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var cancel = new CancellationTokenSource();
        var blocked = new BlockingRange();
        byte[] bytes = [42];
        int attempt = 0;
        using var source = new RangeSource(bytes, (_, _) => ++attempt == 1 ? blocked : new MemoryStream(bytes));
        await using var reader = new ReadAheadStream(source, leaveOpen: !disposeReader);
        Task<int> pending = reader.ReadAsync(new byte[1], cancel.Token).AsTask();
        await blocked.Started.Task.WaitAsync(timeout.Token);
        if (disposeReader)
        {
            await reader.DisposeAsync().AsTask().WaitAsync(timeout.Token);
            Assert.False(source.CanRead);
        }
        else
        {
            await cancel.CancelAsync();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(timeout.Token));
        Assert.False(blocked.CanRead);
        if (!disposeReader)
        {
            Assert.Equal(42, reader.ReadByte());
            reader.Dispose();
            Assert.True(source.CanRead);
        }
    }

    private sealed class RangeSource(byte[] bytes, Func<int, int, Stream> open) : MemoryStream(bytes), ITeeRangeReadSource
    {
        internal List<Stream> Ranges { get; } = [];

        public ValueTask<Stream> OpenReadRangeAsync(long offset, long length, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Stream range = open(checked((int)offset), checked((int)length));
            Ranges.Add(range);
            return ValueTask.FromResult(range);
        }
    }

    private sealed class ShortRange(byte[] data, int offset, int length) : MemoryStream(data, offset, length, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 2)], cancellationToken);
    }

    private sealed class BlockingRange : MemoryStream
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
