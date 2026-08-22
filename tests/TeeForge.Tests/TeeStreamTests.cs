using System.Collections.Concurrent;

namespace TeeForge.Tests;

public class TeeStreamTests
{
    [Fact]
    public void Constructor_requires_unique_non_null_destinations()
    {
        Assert.Throws<ArgumentException>(() => new TeeStream());
        Assert.Throws<ArgumentException>(() => new TeeStream([new MemoryStream(), null!]));

        var stream = new MemoryStream();
        Assert.Throws<ArgumentException>(() => new TeeStream(stream, stream));
    }

    [Fact]
    public void Capabilities_are_intersections()
    {
        using var readable = new MemoryStream();
        using var writeOnly = new CapabilityStream(canRead: false, canWrite: true, canSeek: false, canTimeout: false);
        using var tee = new TeeStream(new TeeStreamOptions(leaveOpen: true), readable, writeOnly);

        Assert.False(tee.CanRead);
        Assert.True(tee.CanWrite);
        Assert.False(tee.CanSeek);
        Assert.False(tee.CanTimeout);
    }

    [Fact]
    public void Write_attempts_every_destination_and_aggregates_in_index_order()
    {
        var calls = new ConcurrentQueue<int>();
        using var first = new ThrowingOperationStream(0, calls, throwOnWrite: true);
        using var middle = new ThrowingOperationStream(1, calls, throwOnWrite: false);
        using var last = new ThrowingOperationStream(2, calls, throwOnWrite: true);
        using var tee = new TeeStream(new TeeStreamOptions(leaveOpen: true), first, middle, last);

        AggregateException exception = Assert.Throws<AggregateException>(() => tee.Write([1, 2, 3]));

        Assert.Equal([0, 1, 2], calls);
        Assert.Collection(
            exception.InnerExceptions,
            item => Assert.Contains("destination 0", item.Message),
            item => Assert.Contains("destination 2", item.Message));
        Assert.Equal([1, 2, 3], middle.ToArray());
    }

    [Fact]
    public void One_failure_rethrows_original_exception()
    {
        var expected = new TestException("expected");
        using var primary = new ThrowingOperationStream(expected);
        using var mirror = new FlushTrackingStream();
        using var tee = new TeeStream(new TeeStreamOptions(leaveOpen: true), primary, mirror);

        TestException actual = Assert.Throws<TestException>(() => tee.Flush());

        Assert.Same(expected, actual);
        Assert.Equal(1, mirror.FlushCalls);
    }

    [Fact]
    public void Primary_read_failure_still_attempts_every_mirror()
    {
        var expected = new TestException("primary read");
        using var primary = new ThrowingReadStream(expected);
        using var firstMirror = new CountingReadStream([1, 2, 3]);
        using var secondMirror = new CountingReadStream([4, 5, 6]);
        using var tee = new TeeStream(new TeeStreamOptions(leaveOpen: true), primary, firstMirror, secondMirror);

        TestException actual = Assert.Throws<TestException>(() => tee.Read(new byte[3]));

        Assert.Same(expected, actual);
        Assert.Equal(1, firstMirror.ReadCalls);
        Assert.Equal(1, secondMirror.ReadCalls);
    }

    [Fact]
    public void Read_normalizes_short_mirror_reads_and_keeps_positions_aligned()
    {
        byte[] data = [1, 2, 3, 4, 5, 6];
        using var primary = new MemoryStream(data);
        using var mirror = new ChunkedReadStream(data, maximumChunk: 2);
        using var tee = new TeeStream(new TeeStreamOptions(leaveOpen: true), primary, mirror);
        var buffer = new byte[6];

        int count = tee.Read(buffer);

        Assert.Equal(6, count);
        Assert.Equal(data, buffer);
        Assert.Equal(6, primary.Position);
        Assert.Equal(6, mirror.Position);
    }

    [Fact]
    public void Primary_zero_is_returned_without_probing_mirrors()
    {
        using var primary = new MemoryStream();
        using var mirror = new CountingReadStream([1, 2, 3]);
        using var tee = new TeeStream(new TeeStreamOptions(leaveOpen: true), primary, mirror);

        Assert.Equal(0, tee.Read(new byte[8]));
        Assert.Equal(0, mirror.ReadCalls);
        Assert.Equal(0, mirror.Position);
    }

    [Fact]
    public void Default_mismatch_throws_then_continues()
    {
        using var primary = new MemoryStream([1, 2, 3]);
        using var mirror = new MemoryStream([1, 9, 3]);
        using var tee = new TeeStream(new TeeStreamOptions(leaveOpen: true), primary, mirror);
        var buffer = new byte[3];

        TeeStreamConsistencyException exception = Assert.Throws<TeeStreamConsistencyException>(() => tee.Read(buffer));
        Assert.Equal("Read", exception.OperationName);
        TeeStreamMismatch mismatch = Assert.Single(exception.Mismatches);
        Assert.Equal(1, mismatch.DestinationIndex);
        Assert.Equal(1, mismatch.FirstDifferingByteOffset);

        tee.Position = 0;
        Assert.Equal(0, tee.Position);
    }

    [Fact]
    public void ThrowAndFault_rejects_later_operations_after_mismatch()
    {
        using var primary = new MemoryStream([1]);
        using var mirror = new MemoryStream([2]);
        var options = new TeeStreamOptions(TeeStreamMismatchBehavior.ThrowAndFault, leaveOpen: true);
        using var tee = new TeeStream(options, primary, mirror);

        Assert.Throws<TeeStreamConsistencyException>(() => tee.ReadByte());
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => tee.Flush());
        Assert.IsType<TeeStreamConsistencyException>(exception.InnerException);
    }

    [Fact]
    public void UsePrimary_returns_primary_data_on_mismatch()
    {
        using var primary = new MemoryStream([1, 2]);
        using var mirror = new MemoryStream([1, 9]);
        var options = new TeeStreamOptions(TeeStreamMismatchBehavior.UsePrimary, leaveOpen: true);
        using var tee = new TeeStream(options, primary, mirror);
        var buffer = new byte[2];

        Assert.Equal(2, tee.Read(buffer));
        Assert.Equal([1, 2], buffer);
    }

    [Fact]
    public void Numeric_results_are_compared()
    {
        using var primary = new MemoryStream(new byte[8]);
        using var mirror = new OffsetSeekStream(resultOffset: 1);
        mirror.SetLength(8);
        using var tee = new TeeStream(new TeeStreamOptions(leaveOpen: true), primary, mirror);

        TeeStreamConsistencyException exception = Assert.Throws<TeeStreamConsistencyException>(
            () => tee.Seek(3, SeekOrigin.Begin));

        Assert.Equal("Seek", exception.OperationName);
        Assert.Equal(3, exception.PrimaryResult);
        Assert.Equal(4, Assert.Single(exception.Mismatches).DestinationResult);
    }

    [Fact]
    public async Task Async_write_starts_all_destinations_before_waiting()
    {
        var first = new GatedWriteStream();
        var second = new GatedWriteStream();
        await using var tee = new TeeStream(new TeeStreamOptions(leaveOpen: true), first, second);

        ValueTask write = tee.WriteAsync(new byte[] { 1 });
        await Task.WhenAll(first.Started, second.Started).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(write.IsCompleted);

        first.Release();
        second.Release();
        await write;
    }

    [Fact]
    public async Task Pre_canceled_async_operation_invokes_no_destination()
    {
        var stream = new GatedWriteStream();
        await using var tee = new TeeStream(new TeeStreamOptions(leaveOpen: true), stream);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await tee.WriteAsync(new byte[] { 1 }, cancellation.Token));
        Assert.False(stream.Started.IsCompleted);
    }

    [Fact]
    public async Task All_async_cancellation_failures_cancel_the_operation()
    {
        await using var first = new AsyncFailureStream(new OperationCanceledException("first"));
        await using var second = new AsyncFailureStream(new TaskCanceledException("second"));
        await using var tee = new TeeStream(new TeeStreamOptions(leaveOpen: true), first, second);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await tee.WriteAsync(new byte[] { 1 }));
        Assert.Equal(1, first.WriteCalls);
        Assert.Equal(1, second.WriteCalls);
    }

    [Fact]
    public async Task Ordinary_and_cancellation_failures_are_aggregated_in_index_order()
    {
        var expected = new TestException("ordinary");
        await using var canceled = new AsyncFailureStream(new OperationCanceledException("canceled"));
        await using var failed = new AsyncFailureStream(expected);
        await using var tee = new TeeStream(new TeeStreamOptions(leaveOpen: true), canceled, failed);

        AggregateException exception = await Assert.ThrowsAsync<AggregateException>(
            async () => await tee.WriteAsync(new byte[] { 1 }));

        Assert.IsType<OperationCanceledException>(exception.InnerExceptions[0]);
        Assert.Same(expected, exception.InnerExceptions[1]);
    }

    [Fact]
    public async Task Concurrent_sync_mode_starts_every_destination_before_waiting()
    {
        using var first = new GatedSyncWriteStream();
        using var second = new GatedSyncWriteStream();
        using var tee = new TeeStream(
            new TeeStreamOptions(synchronousMode: TeeStreamSynchronousMode.Concurrent, leaveOpen: true),
            first,
            second);

        Task write = Task.Run(() => tee.Write(new byte[] { 1 }));
        await Task.WhenAll(first.Started, second.Started).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(write.IsCompleted);

        first.Release();
        second.Release();
        await write;
    }

    [Fact]
    public void Dispose_attempts_all_owned_destinations()
    {
        var first = new DisposeTrackingStream(throwOnDispose: true);
        var second = new DisposeTrackingStream(throwOnDispose: false);
        var tee = new TeeStream(first, second);

        Assert.Throws<TestException>(() => tee.Dispose());
        Assert.True(first.WasDisposed);
        Assert.True(second.WasDisposed);
    }

    private sealed class TestException : Exception
    {
        public TestException(string message)
            : base(message)
        {
        }
    }

    private sealed class CapabilityStream : MemoryStream
    {
        private readonly bool _canRead;
        private readonly bool _canWrite;
        private readonly bool _canSeek;
        private readonly bool _canTimeout;

        public CapabilityStream(bool canRead, bool canWrite, bool canSeek, bool canTimeout)
        {
            _canRead = canRead;
            _canWrite = canWrite;
            _canSeek = canSeek;
            _canTimeout = canTimeout;
        }

        public override bool CanRead => _canRead;
        public override bool CanWrite => _canWrite;
        public override bool CanSeek => _canSeek;
        public override bool CanTimeout => _canTimeout;
    }

    private sealed class ThrowingOperationStream : MemoryStream
    {
        private readonly int _index;
        private readonly ConcurrentQueue<int>? _calls;
        private readonly bool _throwOnWrite;
        private readonly Exception? _flushException;

        public ThrowingOperationStream(int index, ConcurrentQueue<int> calls, bool throwOnWrite)
        {
            _index = index;
            _calls = calls;
            _throwOnWrite = throwOnWrite;
        }

        public ThrowingOperationStream(Exception flushException) => _flushException = flushException;

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _calls!.Enqueue(_index);
            if (_throwOnWrite)
            {
                throw new TestException($"destination {_index}");
            }

            base.Write(buffer);
        }

        public override void Flush()
        {
            if (_flushException is not null)
            {
                throw _flushException;
            }

            base.Flush();
        }
    }

    private sealed class ChunkedReadStream : MemoryStream
    {
        private readonly int _maximumChunk;

        public ChunkedReadStream(byte[] data, int maximumChunk)
            : base(data)
        {
            _maximumChunk = maximumChunk;
        }

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, _maximumChunk)]);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, _maximumChunk)], cancellationToken);
    }

    private sealed class CountingReadStream : MemoryStream
    {
        public CountingReadStream(byte[] data)
            : base(data)
        {
        }

        public int ReadCalls { get; private set; }

        public override int Read(Span<byte> buffer)
        {
            ReadCalls++;
            return base.Read(buffer);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            return base.Read(buffer, offset, count);
        }
    }

    private sealed class FlushTrackingStream : MemoryStream
    {
        public int FlushCalls { get; private set; }

        public override void Flush()
        {
            FlushCalls++;
            base.Flush();
        }
    }

    private sealed class ThrowingReadStream : MemoryStream
    {
        private readonly Exception _exception;

        public ThrowingReadStream(Exception exception) => _exception = exception;

        public override int Read(Span<byte> buffer) => throw _exception;
    }

    private sealed class OffsetSeekStream : MemoryStream
    {
        private readonly long _resultOffset;

        public OffsetSeekStream(long resultOffset) => _resultOffset = resultOffset;

        public override long Seek(long offset, SeekOrigin loc) => base.Seek(offset, loc) + _resultOffset;
    }

    private sealed class GatedWriteStream : MemoryStream
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public void Release() => _release.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
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
            _release.Wait(TimeSpan.FromSeconds(5));
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

    private sealed class AsyncFailureStream : MemoryStream
    {
        private readonly Exception _exception;

        public AsyncFailureStream(Exception exception) => _exception = exception;

        public int WriteCalls { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            return ValueTask.FromException(_exception);
        }
    }

    private sealed class DisposeTrackingStream : MemoryStream
    {
        private readonly bool _throwOnDispose;

        public DisposeTrackingStream(bool throwOnDispose) => _throwOnDispose = throwOnDispose;

        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
            if (_throwOnDispose)
            {
                throw new TestException("dispose");
            }
        }
    }
}
