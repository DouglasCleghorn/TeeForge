using TeeForge.Composition;

namespace TeeForge.Tests;

public class HandoffStreamTests
{
    [Fact]
    public void Handoff_to_buffered_stream_keeps_the_stable_endpoint()
    {
        using var destination = new MemoryStream();
        using var stream = new HandoffStream(destination, leaveOpen: true);

        stream.Write([1, 2]);
        var buffer = new BufferedStream(destination, bufferSize: 8);
        stream.Handoff(buffer);
        stream.Write([3, 4]);

        Assert.Equal([1, 2], destination.ToArray());

        stream.Flush();

        Assert.Equal([1, 2, 3, 4], destination.ToArray());
    }

    [Fact]
    public async Task HandoffAsync_waits_for_an_in_flight_operation()
    {
        using var destination = new BlockingWriteStream();
        await using var stream = new HandoffStream(destination, leaveOpen: true);

        Task write = stream.WriteAsync(new byte[] { 1, 2 }).AsTask();
        await destination.WriteStarted;

        var buffer = new BufferedStream(destination, bufferSize: 8);
        ValueTask handoff = stream.HandoffAsync(buffer);
        Assert.False(handoff.IsCompleted);

        destination.AllowWriteToComplete();
        await write;
        await handoff;

        await stream.WriteAsync(new byte[] { 3, 4 });
        Assert.Equal([1, 2], destination.ToArray());

        await stream.FlushAsync();
        Assert.Equal([1, 2, 3, 4], destination.ToArray());
    }

    [Fact]
    public void Failed_outgoing_flush_keeps_the_previous_pipeline()
    {
        using var destination = new ThrowingFlushStream();
        using var stream = new HandoffStream(destination, leaveOpen: true);
        using var replacement = new MemoryStream();

        Assert.Throws<InvalidOperationException>(
            () => stream.Handoff(replacement));

        stream.Write([1, 2, 3]);
        Assert.Equal([1, 2, 3], destination.ToArray());
        Assert.Empty(replacement.ToArray());
    }

    [Fact]
    public void Handoff_can_change_reported_capabilities()
    {
        using var destination = new MemoryStream();
        using var stream = new HandoffStream(destination, leaveOpen: true);

        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);

        stream.Handoff(new WriteOnlyStream(destination));

        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.True(stream.CanWrite);
        Assert.False(stream.CanReadAt);
        Assert.False(stream.CanWriteAt);
    }

    [Fact]
    public async Task Random_access_survives_handoff_to_standard_buffered_stream()
    {
        using var destination = new MemoryStream();
        destination.Write([0, 1, 2, 3, 4, 5]);
        await using var stream = new HandoffStream(destination, leaveOpen: true);
        stream.Handoff(new BufferedStream(destination, bufferSize: 8));

        Assert.True(stream.CanReadAt);
        Assert.True(stream.CanWriteAt);

        await stream.WriteAsync(new byte[] { 6 });
        long position = stream.Position;
        await stream.WriteAtAsync(new byte[] { 9 }, offset: 1);

        byte[] read = new byte[3];
        Assert.Equal(3, await stream.ReadAtAsync(read, offset: 0));
        Assert.Equal([0, 9, 2], read);
        Assert.Equal(position, stream.Position);

        await stream.FlushAsync();
        Assert.Equal([0, 9, 2, 3, 4, 5, 6], destination.ToArray());
    }

    [Fact]
    public void Dispose_disposes_the_current_pipeline_by_default()
    {
        var destination = new TrackingMemoryStream();
        var stream = new HandoffStream(destination);
        stream.Handoff(new BufferedStream(destination, bufferSize: 8));
        stream.Write([1, 2, 3]);

        stream.Dispose();

        Assert.True(destination.IsDisposed);
        Assert.False(stream.CanRead);
        Assert.False(stream.CanWrite);
    }

    [Fact]
    public void LeaveOpen_leaves_the_current_pipeline_open()
    {
        var destination = new TrackingMemoryStream();
        var stream = new HandoffStream(destination, leaveOpen: true);

        stream.Dispose();

        Assert.False(destination.IsDisposed);
        destination.WriteByte(1);
        destination.Dispose();
    }

    [Fact]
    public void LeaveOpen_flushes_an_inserted_buffer_without_closing_the_source()
    {
        var destination = new TrackingMemoryStream();
        var stream = new HandoffStream(destination, leaveOpen: true);
        stream.Handoff(new BufferedStream(destination, bufferSize: 8));
        stream.Write([1, 2, 3]);

        stream.Dispose();

        Assert.Equal([1, 2, 3], destination.ToArray());
        Assert.False(destination.IsDisposed);
        destination.Dispose();
    }

    private sealed class BlockingWriteStream : MemoryStream
    {
        private readonly TaskCompletionSource _allowWrite = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _writeStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WriteStarted => _writeStarted.Task;

        public void AllowWriteToComplete() => _allowWrite.TrySetResult();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _writeStarted.TrySetResult();
            await _allowWrite.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class TrackingMemoryStream : MemoryStream
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingFlushStream : MemoryStream
    {
        private bool _throwOnNextFlush = true;

        public override void Flush()
        {
            if (_throwOnNextFlush)
            {
                _throwOnNextFlush = false;
                throw new InvalidOperationException("flush failed");
            }

            base.Flush();
        }
    }

    private sealed class WriteOnlyStream(Stream stream) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => stream.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => stream.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            stream.Write(buffer, offset, count);
    }
}
