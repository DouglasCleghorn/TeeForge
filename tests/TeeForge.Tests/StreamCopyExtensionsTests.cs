using TeeForge.Broadcasting;

namespace TeeForge.Tests;

public class StreamCopyExtensionsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Copies_short_reads_to_every_destination_from_current_positions_without_flushing_or_closing()
    {
        byte[] payload = Enumerable.Range(0, 100_003).Select(static value => (byte)value).ToArray();
        await using var source = new BroadcastStreamTests.ChunkedSource(payload, 13);
        source.Position = 9;
        await using var first = new TrackingDestination();
        await using var second = new TrackingDestination();
        first.WriteByte(42);
        second.WriteByte(43);

        await source.CopyToAsync(first, second).WaitAsync(Timeout);

        Assert.Equal(new byte[] { 42 }.Concat(payload[9..]), first.ToArray());
        Assert.Equal(new byte[] { 43 }.Concat(payload[9..]), second.ToArray());
        Assert.True(source.CanRead);
        Assert.True(first.CanWrite);
        Assert.True(second.CanWrite);
        Assert.Equal(0, first.FlushCalls + second.FlushCalls);
    }

    [Fact]
    public async Task Fast_destination_advances_while_slow_destination_holds_shared_memory()
    {
        byte[] payload = Enumerable.Range(0, 128).Select(static value => (byte)value).ToArray();
        await using var source = new MemoryStream(payload);
        await using var slow = new GatedDestination();
        await using var fast = new TrackingDestination(signalAt: 12);
        var options = new BroadcastCopyOptions(bufferSize: 4, pauseWriterThreshold: 16, resumeWriterThreshold: 8);
        Task copy = source.CopyToAsync([slow, fast], options);
        try
        {
            await Task.WhenAll(slow.Entered.Task, fast.Reached.Task).WaitAsync(Timeout);
            Assert.False(copy.IsCompleted);
            Assert.Equal(0, slow.Length);
            Assert.Equal(payload[..4], slow.PendingBuffer.ToArray());
            Assert.InRange(source.Position, 12, 16);
        }
        finally
        {
            slow.Release.TrySetResult();
        }

        await copy.WaitAsync(Timeout);
        Assert.Equal(payload, slow.ToArray());
        Assert.Equal(payload, fast.ToArray());
    }

    [Fact]
    public async Task Default_failure_cancels_inflight_copies_and_reports_destination_index()
    {
        await using var source = new MemoryStream(new byte[1024]);
        await using var waiting = new GatedDestination();
        var expected = new IOException("Destination failed.");
        await using var failing = new FailingDestination(expected, waitFor: waiting.Entered.Task);
        var options = new BroadcastCopyOptions(bufferSize: 4, pauseWriterThreshold: 8, resumeWriterThreshold: 4);

        AggregateException aggregate = await Assert.ThrowsAsync<AggregateException>(
            () => source.CopyToAsync([waiting, failing], options).WaitAsync(Timeout));

        var failure = Assert.IsType<BroadcastCopyDestinationException>(Assert.Single(aggregate.InnerExceptions));
        Assert.Equal(1, failure.DestinationIndex);
        Assert.Same(expected, failure.InnerException);
        Assert.True(waiting.CancellationObserved);
        Assert.InRange(source.Position, 1, 8);
        Assert.True(source.CanRead);
        Assert.True(waiting.CanWrite);
        Assert.True(failing.CanWrite);
        Assert.Equal(0, waiting.FlushCalls + failing.FlushCalls);
    }

    [Fact]
    public async Task Continue_finishes_healthy_destinations_then_reports_failures_in_input_order()
    {
        byte[] payload = Enumerable.Range(0, 1000).Select(static value => (byte)value).ToArray();
        await using var source = new MemoryStream(payload);
        var firstError = new IOException("first");
        var secondError = new IOException("second");
        await using var first = new FailingDestination(firstError);
        await using var healthy = new TrackingDestination();
        await using var second = new FailingDestination(secondError);
        var options = new BroadcastCopyOptions(bufferSize: 7, pauseWriterThreshold: 28, resumeWriterThreshold: 14,
            failureBehavior: BroadcastCopyFailureBehavior.Continue);

        AggregateException aggregate = await Assert.ThrowsAsync<AggregateException>(
            () => source.CopyToAsync([first, healthy, second], options).WaitAsync(Timeout));

        Assert.Equal(payload, healthy.ToArray());
        BroadcastCopyDestinationException[] failures = aggregate.InnerExceptions.Cast<BroadcastCopyDestinationException>().ToArray();
        Assert.Equal([0, 2], failures.Select(static failure => failure.DestinationIndex));
        Assert.Same(firstError, failures[0].InnerException);
        Assert.Same(secondError, failures[1].InnerException);
        Assert.Equal(payload.Length, source.Position);
        Assert.True(healthy.CanWrite);
        Assert.Equal(0, healthy.FlushCalls);
    }

    [Fact]
    public async Task Stop_waits_for_an_uncancellable_write_before_releasing_its_buffer()
    {
        byte[] payload = Enumerable.Range(0, 128).Select(static value => (byte)value).ToArray();
        await using var source = new MemoryStream(payload);
        await using var waiting = new UncancellableDestination();
        await using var failing = new FailingDestination(new IOException("failed"), waiting.Entered.Task);
        Task copy = source.CopyToAsync([waiting, failing],
            new BroadcastCopyOptions(bufferSize: 4, pauseWriterThreshold: 8, resumeWriterThreshold: 4));
        try
        {
            await waiting.Canceled.Task.WaitAsync(Timeout);
            Assert.False(copy.IsCompleted);
            Assert.Equal(payload[..4], waiting.PendingBuffer.ToArray());
        }
        finally
        {
            waiting.Release.TrySetResult();
        }

        await Assert.ThrowsAsync<AggregateException>(() => copy.WaitAsync(Timeout));
        Assert.Equal(payload[..4], waiting.ToArray());
    }

    [Fact]
    public async Task Continue_stops_the_source_when_no_destinations_remain()
    {
        await using var source = new MemoryStream(new byte[1024]);
        await using var first = new FailingDestination(new IOException("first"));
        await using var second = new FailingDestination(new IOException("second"));
        var options = new BroadcastCopyOptions(bufferSize: 4, pauseWriterThreshold: 4, resumeWriterThreshold: 2,
            failureBehavior: BroadcastCopyFailureBehavior.Continue);

        AggregateException aggregate = await Assert.ThrowsAsync<AggregateException>(
            () => source.CopyToAsync([first, second], options).WaitAsync(Timeout));

        Assert.Equal(2, aggregate.InnerExceptions.OfType<BroadcastCopyDestinationException>().Count());
        Assert.Equal(2, aggregate.InnerExceptions.Count);
        Assert.InRange(source.Position, 1, 4);
    }

    [Theory]
    [InlineData(BroadcastCopyFailureBehavior.Stop)]
    [InlineData(BroadcastCopyFailureBehavior.Continue)]
    public async Task Caller_cancellation_cancels_the_whole_copy_and_preserves_stream_ownership(BroadcastCopyFailureBehavior behavior)
    {
        await using var source = new MemoryStream(new byte[1000]);
        await using var first = new GatedDestination();
        await using var second = new GatedDestination();
        using var cancellation = new CancellationTokenSource();
        Task copy = source.CopyToAsync([first, second], new BroadcastCopyOptions(failureBehavior: behavior), cancellation.Token);
        await Task.WhenAll(first.Entered.Task, second.Entered.Task).WaitAsync(Timeout);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy.WaitAsync(Timeout));
        Assert.True(copy.IsCanceled);
        Assert.True(first.CancellationObserved);
        Assert.True(second.CancellationObserved);
        Assert.True(source.CanRead);
        Assert.True(first.CanWrite);
        Assert.True(second.CanWrite);
    }

    [Fact]
    public async Task Pre_canceled_copy_does_not_read_the_source()
    {
        await using var source = new MemoryStream([1, 2]);
        await using var destination = new TrackingDestination();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.CopyToAsync([destination], cancellation.Token));
        Assert.Equal(0, source.Position);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task Source_failure_is_reported_once_after_published_bytes_reach_destinations()
    {
        byte[] payload = [1, 2, 3];
        await using var source = new BroadcastStreamTests.FailingSource(payload);
        await using var first = new TrackingDestination();
        await using var second = new TrackingDestination();
        AggregateException aggregate = await Assert.ThrowsAsync<AggregateException>(() => source.CopyToAsync(first, second));
        Assert.IsType<IOException>(Assert.Single(aggregate.InnerExceptions));
        Assert.Equal(payload, first.ToArray());
        Assert.Equal(payload, second.ToArray());
    }

    [Fact]
    public async Task Destination_cancellation_without_caller_cancellation_is_a_destination_failure()
    {
        await using var source = new MemoryStream([1]);
        var expected = new OperationCanceledException("Destination stopped itself.");
        await using var destination = new FailingDestination(expected);
        AggregateException aggregate = await Assert.ThrowsAsync<AggregateException>(() => source.CopyToAsync([destination]));
        var failure = Assert.IsType<BroadcastCopyDestinationException>(Assert.Single(aggregate.InnerExceptions));
        Assert.Same(expected, failure.InnerException);
    }

    [Fact]
    public async Task Destinations_are_snapshotted_once_before_the_source_is_read()
    {
        await using var source = new BroadcastStreamTests.GatedSource([1, 2]);
        await using var first = new TrackingDestination();
        await using var second = new TrackingDestination();
        await using var replacement = new TrackingDestination();
        Stream[] destinations = [first, second];
        int enumerations = 0;
        IEnumerable<Stream> Enumerate()
        {
            enumerations++;
            foreach (Stream destination in destinations)
            {
                yield return destination;
            }
        }

        Task copy = source.CopyToAsync(Enumerate());
        await source.Entered.Task.WaitAsync(Timeout);
        destinations[1] = replacement;
        source.Release.TrySetResult();
        await copy.WaitAsync(Timeout);
        Assert.Equal(1, enumerations);
        Assert.Equal(new byte[] { 1, 2 }, second.ToArray());
        Assert.Equal(0, replacement.Length);
    }

    [Fact]
    public void Invalid_destinations_are_rejected_before_any_source_io()
    {
        using var source = new MemoryStream([1, 2]);
        using var destination = new MemoryStream();
        using var readOnly = new MemoryStream([1], writable: false);
        Assert.Throws<ArgumentException>(() => { _ = source.CopyToAsync(Array.Empty<Stream>()); });
        Assert.Throws<ArgumentException>(() => { _ = source.CopyToAsync(destination, destination); });
        Assert.Throws<ArgumentException>(() => { _ = source.CopyToAsync(source, destination); });
        Assert.Throws<ArgumentException>(() => { _ = source.CopyToAsync(destination, null!); });
        Assert.Throws<ArgumentException>(() => { _ = source.CopyToAsync(destination, readOnly); });
        Assert.Throws<ArgumentNullException>(() => { _ = StreamCopyExtensions.CopyToAsync(null!, destination); });
        Assert.Throws<ArgumentNullException>(() => { _ = source.CopyToAsync((IEnumerable<Stream>)null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = source.CopyToAsync([destination], (BroadcastCopyOptions)null!); });
        Assert.Equal(0, source.Position);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task Single_destination_instance_overloads_and_collection_extensions_both_work()
    {
        await using var source = new MemoryStream([1, 2]);
        await using var destination = new MemoryStream();
        await source.CopyToAsync(destination);
        source.Position = 0;
        await source.CopyToAsync([destination]);
        Assert.Equal(new byte[] { 1, 2, 1, 2 }, destination.ToArray());
    }

    private class TrackingDestination(int signalAt = int.MaxValue) : MemoryStream
    {
        internal int FlushCalls { get; private set; }
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            if (Length >= signalAt)
            {
                Reached.TrySetResult();
            }
        }

        public override void Flush() => FlushCalls++;
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class GatedDestination : TrackingDestination
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ReadOnlyMemory<byte> PendingBuffer { get; private set; }
        internal bool CancellationObserved { get; private set; }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            PendingBuffer = buffer;
            Entered.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }

            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class FailingDestination(Exception failure, Task? waitFor = null) : TrackingDestination
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (waitFor is not null)
            {
                await waitFor.WaitAsync(cancellationToken);
            }

            throw failure;
        }
    }

    private sealed class UncancellableDestination : TrackingDestination
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ReadOnlyMemory<byte> PendingBuffer { get; private set; }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            PendingBuffer = buffer;
            using CancellationTokenRegistration registration = cancellationToken.Register(() => Canceled.TrySetResult());
            Entered.TrySetResult();
            await Release.Task;
            await base.WriteAsync(buffer, CancellationToken.None);
        }
    }
}
