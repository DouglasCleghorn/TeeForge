using TeeForge.Broadcasting;

namespace TeeForge.Tests;

public class BroadcastStreamTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Readers_independently_receive_short_source_reads_from_the_initial_position()
    {
        byte[] payload = Enumerable.Range(0, 100_003).Select(static value => (byte)value).ToArray();
        await using var source = new ChunkedSource(payload, chunkSize: 13);
        source.Position = 7;
        await using var broadcast = new BroadcastStream(source, 3, new BroadcastStreamOptions(leaveOpen: true));

        byte[][] received = await Task.WhenAll(
            ReadAllAsync(broadcast.Readers[0], 7),
            ReadAllAsync(broadcast.Readers[1], 127),
            ReadAllAsync(broadcast.Readers[2], 8192)).WaitAsync(Timeout);
        await broadcast.Completion.WaitAsync(Timeout);

        Assert.All(received, bytes => Assert.Equal(payload[7..], bytes));
        Assert.Equal(payload.Length - 7, broadcast.BytesBroadcast);
        Assert.All(broadcast.Readers, reader => Assert.Equal(payload.Length - 7, reader.Position));
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task Slow_reader_retains_bytes_and_disposal_releases_backpressure()
    {
        byte[] payload = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        await using var source = new MemoryStream(payload);
        await using var broadcast = new BroadcastStream(source, 2,
            new BroadcastStreamOptions(bufferSize: 4, pauseWriterThreshold: 4, resumeWriterThreshold: 2));
        Stream fast = broadcast.Readers[0];
        Stream slow = broadcast.Readers[1];

        byte[] prefix = new byte[4];
        await fast.ReadExactlyAsync(prefix).AsTask().WaitAsync(Timeout);
        Assert.Equal(payload[..4], prefix);
        Assert.Equal(4, fast.Position);
        Assert.Equal(0, slow.Position);
        Assert.Equal(4, source.Position);

        byte[] next = new byte[1];
        Task<int> pending = fast.ReadAsync(next).AsTask();
        Assert.False(pending.IsCompleted);
        byte[] slowPrefix = new byte[3];
        await slow.ReadExactlyAsync(slowPrefix).AsTask().WaitAsync(Timeout);
        Assert.Equal(payload[..3], slowPrefix);
        Assert.Equal(1, await pending.WaitAsync(Timeout));
        Assert.Equal(payload[4], next[0]);

        await slow.DisposeAsync();
        byte[] rest = await ReadAllAsync(fast, 3).WaitAsync(Timeout);
        Assert.Equal(payload[5..], rest);
        await broadcast.Completion.WaitAsync(Timeout);
        Assert.False(slow.CanRead);
    }

    [Fact]
    public async Task Reader_streams_support_sync_array_span_byte_and_copy_reads()
    {
        byte[] payload = [1, 2, 3, 4, 5, 6];
        await using var source = new MemoryStream(payload);
        await using var broadcast = new BroadcastStream(source, 2);
        await broadcast.Completion.WaitAsync(Timeout);
        Stream reader = broadcast.Readers[0];
        Assert.True(reader.CanRead);
        Assert.False(reader.CanWrite);
        Assert.False(reader.CanSeek);
        Assert.Throws<NotSupportedException>(() => reader.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => reader.WriteByte(9));
        Assert.Equal(0, reader.Read(Span<byte>.Empty));
        Assert.Equal(1, reader.ReadByte());
        byte[] bytes = new byte[2];
        Assert.Equal(2, reader.Read(bytes, 0, 2));
        Assert.Equal(new byte[] { 2, 3 }, bytes);
        Assert.Equal(2, reader.Read(bytes.AsSpan()));
        Assert.Equal(new byte[] { 4, 5 }, bytes);
#pragma warning disable CA1835 // Exercise the legacy array overload explicitly.
        Assert.Equal(1, await reader.ReadAsync(bytes, 0, 1, CancellationToken.None));
#pragma warning restore CA1835
        Assert.Equal(6, bytes[0]);
        Assert.Equal(-1, reader.ReadByte());

        using var destination = new MemoryStream();
        broadcast.Readers[1].CopyTo(destination, 2);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact]
    public async Task Source_failure_is_reported_after_each_reader_drains_its_buffer()
    {
        byte[] payload = [1, 2, 3];
        await using var source = new FailingSource(payload);
        await using var broadcast = new BroadcastStream(source, 2);
        IOException failure = await Assert.ThrowsAsync<IOException>(() => broadcast.Completion.WaitAsync(Timeout));
        foreach (Stream reader in broadcast.Readers)
        {
            using var destination = new MemoryStream();
            IOException observed = await Assert.ThrowsAsync<IOException>(() => reader.CopyToAsync(destination));
            Assert.Same(failure, observed);
            Assert.Equal(payload, destination.ToArray());
        }
    }

    [Fact]
    public async Task Canceling_one_read_leaves_its_cursor_and_other_readers_available()
    {
        await using var source = new GatedSource([1, 2, 3]);
        await using var broadcast = new BroadcastStream(source, 2);
        await source.Entered.Task.WaitAsync(Timeout);
        using var cancellation = new CancellationTokenSource();
        Task<int> pending = broadcast.Readers[0].ReadAsync(new byte[3], cancellation.Token).AsTask();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Timeout));
        Assert.Equal(0, broadcast.Readers[0].Position);
        source.Release.TrySetResult();
        byte[][] received = await Task.WhenAll(broadcast.Readers.Select(reader => ReadAllAsync(reader, 2))).WaitAsync(Timeout);
        Assert.All(received, bytes => Assert.Equal(new byte[] { 1, 2, 3 }, bytes));
    }

    [Fact]
    public async Task Disposing_all_readers_cancels_an_inflight_source_read()
    {
        await using var source = new GatedSource([1]);
        await using var broadcast = new BroadcastStream(source, 2, new BroadcastStreamOptions(leaveOpen: true));
        await source.Entered.Task.WaitAsync(Timeout);
        broadcast.Readers[0].Dispose();
        Assert.False(broadcast.Completion.IsCompleted);
        await broadcast.Readers[1].DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => broadcast.Completion.WaitAsync(Timeout));
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task Disposing_a_reader_with_a_pending_read_releases_it_without_stopping_siblings()
    {
        await using var source = new GatedSource([1, 2]);
        await using var broadcast = new BroadcastStream(source, 2);
        Task<int> pending = broadcast.Readers[0].ReadAsync(new byte[1]).AsTask();
        await source.Entered.Task.WaitAsync(Timeout);
        await broadcast.Readers[0].DisposeAsync().AsTask().WaitAsync(Timeout);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Timeout));
        source.Release.TrySetResult();
        Assert.Equal(new byte[] { 1, 2 }, await ReadAllAsync(broadcast.Readers[1], 1).WaitAsync(Timeout));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disposal_cancels_pending_reads_is_idempotent_and_honors_source_ownership(bool leaveOpen)
    {
        await using var source = new GatedSource([1]);
        var broadcast = new BroadcastStream(source, 1, new BroadcastStreamOptions(leaveOpen: leaveOpen));
        Task<int> pending = broadcast.Readers[0].ReadAsync(new byte[1]).AsTask();
        await source.Entered.Task.WaitAsync(Timeout);
        await broadcast.DisposeAsync().AsTask().WaitAsync(Timeout);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Timeout));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => broadcast.Completion);
        broadcast.Dispose();
        Assert.Equal(leaveOpen, source.CanRead);
        Assert.False(broadcast.Readers[0].CanRead);
    }

    [Fact]
    public async Task Concurrent_reads_on_the_same_endpoint_are_rejected()
    {
        await using var source = new GatedSource([1]);
        await using var broadcast = new BroadcastStream(source, 1);
        Task<int> pending = broadcast.Readers[0].ReadAsync(new byte[1]).AsTask();
        await Assert.ThrowsAsync<InvalidOperationException>(() => broadcast.Readers[0].ReadAsync(new byte[1]).AsTask());
        source.Release.TrySetResult();
        Assert.Equal(1, await pending.WaitAsync(Timeout));
    }

    [Fact]
    public void Constructors_validate_source_readers_and_buffer_thresholds()
    {
        using var source = new MemoryStream();
        Assert.Throws<ArgumentNullException>(() => new BroadcastStream(null!, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BroadcastStream(source, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BroadcastStreamOptions(bufferSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BroadcastStreamOptions(pauseWriterThreshold: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BroadcastStreamOptions(resumeWriterThreshold: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BroadcastStreamOptions(pauseWriterThreshold: 1, resumeWriterThreshold: 2));
        source.Dispose();
        Assert.Throws<ArgumentException>(() => new BroadcastStream(source, 1));
    }

    internal static async Task<byte[]> ReadAllAsync(Stream reader, int bufferSize)
    {
        using var destination = new MemoryStream();
        await reader.CopyToAsync(destination, bufferSize);
        return destination.ToArray();
    }

    internal sealed class ChunkedSource(byte[] payload, int chunkSize) : MemoryStream(payload, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }

    internal sealed class FailingSource(byte[] payload) : MemoryStream(payload, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position == Length
                ? ValueTask.FromException<int>(new IOException("Source failed."))
                : base.ReadAsync(buffer, cancellationToken);
    }

    internal sealed class GatedSource(byte[] payload) : MemoryStream(payload, writable: false)
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
