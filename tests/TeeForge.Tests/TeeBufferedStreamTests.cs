namespace TeeForge.Tests;

public class TeeBufferedStreamTests
{
    [Fact]
    public void Buffered_options_inherit_tee_behavior_and_own_buffer_size()
    {
        var options = new TeeBufferedStreamOptions(
            TeeStreamMismatchBehavior.UsePrimary,
            TeeStreamSynchronousMode.Concurrent,
            leaveOpen: true,
            bufferSize: 1234);

        Assert.IsAssignableFrom<TeeStreamOptions>(options);
        Assert.Equal(TeeStreamMismatchBehavior.UsePrimary, options.MismatchBehavior);
        Assert.Equal(TeeStreamSynchronousMode.Concurrent, options.SynchronousMode);
        Assert.True(options.LeaveOpen);
        Assert.Equal(1234, options.BufferSize);
        Assert.Equal(4096, TeeBufferedStreamOptions.Default.BufferSize);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TeeBufferedStreamOptions(bufferSize: 0));
    }

    [Fact]
    public void Small_writes_are_shared_and_buffered_until_flush()
    {
        using var first = new WriteTrackingStream();
        using var second = new WriteTrackingStream();
        using var stream = new TeeBufferedStream(
            [first, second],
            new TeeBufferedStreamOptions(leaveOpen: true, bufferSize: 16));

        stream.Write([1, 2]);
        stream.Write([3, 4, 5]);

        Assert.Empty(first.ToArray());
        Assert.Empty(second.ToArray());

        stream.Flush();

        Assert.Equal([1, 2, 3, 4, 5], first.ToArray());
        Assert.Equal([1, 2, 3, 4, 5], second.ToArray());
        Assert.Equal(1, first.WriteCalls);
        Assert.Equal(1, second.WriteCalls);
    }

    [Fact]
    public async Task FlushAsync_broadcasts_buffered_async_writes()
    {
        await using var first = new MemoryStream();
        await using var second = new MemoryStream();
        await using var stream = new TeeBufferedStream(
            [first, second],
            new TeeBufferedStreamOptions(leaveOpen: true, bufferSize: 16));

        await stream.WriteAsync(new byte[] { 1, 2, 3 });
        Assert.Empty(first.ToArray());
        Assert.Empty(second.ToArray());

        await stream.FlushAsync();

        Assert.Equal([1, 2, 3], first.ToArray());
        Assert.Equal([1, 2, 3], second.ToArray());
    }

    [Fact]
    public void Capabilities_are_the_buffered_intersection_of_destinations()
    {
        using var readable = new MemoryStream();
        using var writeOnly = new WriteOnlyStream();
        using var stream = new TeeBufferedStream(
            [readable, writeOnly],
            new TeeBufferedStreamOptions(leaveOpen: true));

        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.True(stream.CanWrite);
    }

    [Fact]
    public void Destination_capabilities_are_snapshotted_for_buffered_hot_paths()
    {
        using var first = new CapabilityTrackingStream();
        using var second = new CapabilityTrackingStream();
        using var stream = new TeeBufferedStream(
            [first, second],
            new TeeBufferedStreamOptions(leaveOpen: true));

        int firstQueriesAfterConstruction = first.CanWriteQueries;
        int secondQueriesAfterConstruction = second.CanWriteQueries;

        for (int index = 0; index < 100; index++)
        {
            stream.WriteByte((byte)index);
        }

        stream.Flush();

        Assert.Equal(firstQueriesAfterConstruction, first.CanWriteQueries);
        Assert.Equal(secondQueriesAfterConstruction, second.CanWriteQueries);
    }

    [Fact]
    public void Dispose_flushes_the_shared_buffer_and_honors_leave_open()
    {
        using var first = new MemoryStream();
        using var second = new MemoryStream();
        var stream = new TeeBufferedStream(
            [first, second],
            new TeeBufferedStreamOptions(leaveOpen: true, bufferSize: 16));

        stream.Write([1, 2, 3]);
        stream.Dispose();

        Assert.Equal([1, 2, 3], first.ToArray());
        Assert.Equal([1, 2, 3], second.ToArray());
        Assert.True(first.CanWrite);
        Assert.True(second.CanWrite);
    }

    [Fact]
    public void Invalid_buffer_size_does_not_take_ownership_of_destinations()
    {
        using var destination = new MemoryStream();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TeeBufferedStream(0, destination));

        Assert.True(destination.CanWrite);
    }

    private sealed class WriteTrackingStream : MemoryStream
    {
        public int WriteCalls { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteCalls++;
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            WriteCalls++;
            base.Write(buffer);
        }
    }

    private sealed class WriteOnlyStream : MemoryStream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;
    }

    private sealed class CapabilityTrackingStream : Stream
    {
        public int CanWriteQueries { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite
        {
            get
            {
                CanWriteQueries++;
                return true;
            }
        }

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }
}
