using System.Collections.Concurrent;

namespace TeeForge.Tests;

public class ReplicaStreamTests
{
    [Fact]
    public void Construction_requires_unique_writable_replicas()
    {
        using var writable = new MemoryStream();
        using var readOnly = new MemoryStream([1, 2, 3], writable: false);

        Assert.Throws<ArgumentException>(() => new ReplicaStream());
        Assert.Throws<ArgumentException>(() => new ReplicaStream([writable, null!]));
        Assert.Throws<ArgumentException>(() => new ReplicaStream(writable, writable));
        Assert.Throws<ArgumentException>(() => new ReplicaStream(readOnly));
        Assert.Throws<ArgumentNullException>(
            () => new ReplicaStream((ReplicaStreamOptions)null!, writable));
    }

    [Fact]
    public void Options_validate_synchronous_mode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ReplicaStreamOptions((TeeStreamSynchronousMode)int.MaxValue));
    }

    [Fact]
    public void Stream_is_write_only_and_forward_only()
    {
        using var replica = new MemoryStream();
        using var stream = new ReplicaStream(new ReplicaStreamOptions(leaveOpen: true), replica);

        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.True(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.ReadByte());
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
        Assert.Throws<NotSupportedException>(() => _ = stream.Length);
        Assert.Throws<NotSupportedException>(() => _ = stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Position = 0);
    }

    [Fact]
    public void Write_timeout_is_replicated_and_checked()
    {
        using var first = new TimeoutWriteStream { WriteTimeout = 10 };
        using var second = new TimeoutWriteStream { WriteTimeout = 10 };
        using var stream = new ReplicaStream(
            new ReplicaStreamOptions(leaveOpen: true),
            first,
            second);

        Assert.True(stream.CanTimeout);
        Assert.Equal(10, stream.WriteTimeout);

        stream.WriteTimeout = 20;
        Assert.Equal(20, first.WriteTimeout);
        Assert.Equal(20, second.WriteTimeout);

        second.WriteTimeout = 30;
        Assert.Throws<TeeStreamConsistencyException>(() => _ = stream.WriteTimeout);
    }

    [Fact]
    public async Task Writes_and_flushes_are_replicated()
    {
        await using var first = new TrackingWriteStream();
        await using var second = new TrackingWriteStream();
        await using var stream = new ReplicaStream(
            new ReplicaStreamOptions(leaveOpen: true),
            first,
            second);

        stream.WriteByte(1);
        stream.Write([2, 3]);
        await stream.WriteAsync(new byte[] { 4, 5 });
        await stream.FlushAsync();

        Assert.Equal([1, 2, 3, 4, 5], first.ToArray());
        Assert.Equal([1, 2, 3, 4, 5], second.ToArray());
        Assert.Equal(1, first.FlushAsyncCalls);
        Assert.Equal(1, second.FlushAsyncCalls);
    }

    [Fact]
    public void A_failure_does_not_prevent_later_replicas_from_receiving_the_write()
    {
        var calls = new ConcurrentQueue<int>();
        using var first = new OrderedWriteStream(0, calls, new TestException("first"));
        using var second = new OrderedWriteStream(1, calls);
        using var third = new OrderedWriteStream(2, calls, new TestException("third"));
        using var stream = new ReplicaStream(
            new ReplicaStreamOptions(leaveOpen: true),
            first,
            second,
            third);

        AggregateException exception = Assert.Throws<AggregateException>(() => stream.Write([42]));

        Assert.Equal([0, 1, 2], calls);
        Assert.Equal("first", exception.InnerExceptions[0].Message);
        Assert.Equal("third", exception.InnerExceptions[1].Message);
        Assert.Equal([42], second.ToArray());
    }

    [Fact]
    public async Task Async_writes_start_every_replica_before_awaiting_completion()
    {
        await using var first = new GatedWriteStream();
        await using var second = new GatedWriteStream();
        await using var stream = new ReplicaStream(
            new ReplicaStreamOptions(leaveOpen: true),
            first,
            second);

        ValueTask write = stream.WriteAsync(new byte[] { 1 });
        await Task.WhenAll(first.Started, second.Started).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(write.IsCompleted);

        first.Release();
        second.Release();
        await write;
    }

    [Fact]
    public async Task Concurrent_sync_mode_starts_every_replica_before_waiting()
    {
        using var first = new GatedSyncWriteStream();
        using var second = new GatedSyncWriteStream();
        using var stream = new ReplicaStream(
            new ReplicaStreamOptions(
                synchronousMode: TeeStreamSynchronousMode.Concurrent,
                leaveOpen: true),
            first,
            second);

        Task write = Task.Run(() => stream.Write(new byte[] { 1 }, 0, 1));
        await Task.WhenAll(first.Started, second.Started).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(write.IsCompleted);

        first.Release();
        second.Release();
        await write;
    }

    [Fact]
    public void LeaveOpen_controls_replica_ownership()
    {
        var owned = new TrackingWriteStream();
        var borrowed = new TrackingWriteStream();

        new ReplicaStream(owned).Dispose();
        new ReplicaStream(new ReplicaStreamOptions(leaveOpen: true), borrowed).Dispose();

        Assert.True(owned.WasDisposed);
        Assert.False(borrowed.WasDisposed);
        borrowed.Dispose();
    }

    [Fact]
    public void Disposed_stream_reports_no_capabilities_and_rejects_writes()
    {
        var replica = new TrackingWriteStream();
        var stream = new ReplicaStream(new ReplicaStreamOptions(leaveOpen: true), replica);

        stream.Dispose();

        Assert.False(stream.CanTimeout);
        Assert.False(stream.CanWrite);
        Assert.Throws<ObjectDisposedException>(() => stream.WriteByte(1));
        replica.Dispose();
    }

    private sealed class TrackingWriteStream : MemoryStream
    {
        public int FlushAsyncCalls { get; private set; }

        public bool WasDisposed { get; private set; }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushAsyncCalls++;
            return base.FlushAsync(cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class OrderedWriteStream : MemoryStream
    {
        private readonly int _index;
        private readonly ConcurrentQueue<int> _calls;
        private readonly Exception? _exception;

        public OrderedWriteStream(int index, ConcurrentQueue<int> calls, Exception? exception = null)
        {
            _index = index;
            _calls = calls;
            _exception = exception;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _calls.Enqueue(_index);
            if (_exception is not null)
            {
                throw _exception;
            }

            base.Write(buffer);
        }
    }

    private sealed class TimeoutWriteStream : MemoryStream
    {
        public override bool CanTimeout => true;

        public override int WriteTimeout { get; set; }
    }

    private sealed class GatedWriteStream : MemoryStream
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public void Release() => _release.TrySetResult();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class GatedSyncWriteStream : MemoryStream
    {
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public void Release() => _release.Set();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _started.TrySetResult();
            _release.Wait();
            base.Write(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _release.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class TestException : Exception
    {
        public TestException(string message)
            : base(message)
        {
        }
    }
}
