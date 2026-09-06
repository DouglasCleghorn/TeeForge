using System.Buffers;
using System.IO.Pipelines;

namespace TeeForge.Tests;

public class BroadcastPipeTests
{
    [Fact]
    public void Constructor_requires_positive_reader_count()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BroadcastPipe(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BroadcastPipe(-1));
    }

    [Fact]
    public async Task Every_reader_observes_the_complete_sequence_independently()
    {
        var pipe = new BroadcastPipe(3);
        byte[] payload = Enumerable.Range(0, 100).Select(static value => (byte)value).ToArray();

        FlushResult flush = await pipe.Writer.WriteAsync(payload);
        Assert.False(flush.IsCompleted);

        for (int index = 2; index >= 0; index--)
        {
            ReadResult result = await pipe.Readers[index].ReadAsync();
            Assert.Equal(payload, result.Buffer.ToArray());
            pipe.Readers[index].AdvanceTo(result.Buffer.End);
        }

        pipe.Writer.Complete();
        foreach (PipeReader reader in pipe.Readers)
        {
            ReadResult completed = await reader.ReadAsync();
            Assert.True(completed.IsCompleted);
            reader.AdvanceTo(completed.Buffer.End);
            reader.Complete();
        }
    }

    [Fact]
    public async Task Slowest_reader_controls_backpressure()
    {
        var options = new BroadcastPipeOptions(pauseWriterThreshold: 4, resumeWriterThreshold: 2, useSynchronizationContext: false);
        var pipe = new BroadcastPipe(2, options);

        ValueTask<FlushResult> flush = pipe.Writer.WriteAsync(new byte[4]);
        Assert.False(flush.IsCompleted);

        ReadResult first = await pipe.Readers[0].ReadAsync();
        pipe.Readers[0].AdvanceTo(first.Buffer.End);
        Assert.False(flush.IsCompleted);

        ReadResult second = await pipe.Readers[1].ReadAsync();
        pipe.Readers[1].AdvanceTo(second.Buffer.End);
        Assert.False((await flush).IsCompleted);

        Complete(pipe);
    }

    [Fact]
    public async Task Completing_a_slow_reader_removes_it_from_backpressure()
    {
        var options = new BroadcastPipeOptions(pauseWriterThreshold: 4, resumeWriterThreshold: 2, useSynchronizationContext: false);
        var pipe = new BroadcastPipe(2, options);
        ValueTask<FlushResult> flush = pipe.Writer.WriteAsync(new byte[4]);

        ReadResult active = await pipe.Readers[0].ReadAsync();
        pipe.Readers[0].AdvanceTo(active.Buffer.End);
        Assert.False(flush.IsCompleted);

        pipe.Readers[1].Complete();
        Assert.False((await flush).IsCompleted);

        pipe.Writer.Complete();
        ReadResult completed = await pipe.Readers[0].ReadAsync();
        pipe.Readers[0].AdvanceTo(completed.Buffer.End);
        pipe.Readers[0].Complete();
    }

    [Fact]
    public async Task Flush_is_completed_only_after_final_reader_completes()
    {
        var pipe = new BroadcastPipe(2);
        pipe.Readers[0].Complete();

        FlushResult first = await pipe.Writer.FlushAsync();
        Assert.False(first.IsCompleted);

        pipe.Readers[1].Complete();
        FlushResult second = await pipe.Writer.FlushAsync();
        Assert.True(second.IsCompleted);
        pipe.Writer.Complete();
    }

    [Fact]
    public async Task Reader_completion_tasks_never_fault_and_preserve_indexed_exceptions()
    {
        var pipe = new BroadcastPipe(2);
        var expected = new InvalidDataException("reader one");

        pipe.Readers[1].Complete(expected);
        pipe.Readers[0].Complete();

        Assert.Null(await pipe.ReaderCompletions[0]);
        Assert.Same(expected, await pipe.ReaderCompletions[1]);
        Assert.Equal(TaskStatus.RanToCompletion, pipe.ReaderCompletions[1].Status);
        pipe.Writer.Complete();
    }

    [Fact]
    public async Task Continue_mode_isolates_reader_failure()
    {
        var pipe = new BroadcastPipe(2);
        var expected = new InvalidDataException("isolated");
        pipe.Readers[0].Complete(expected);

        FlushResult flush = await pipe.Writer.WriteAsync(new byte[] { 7, 8 });
        Assert.False(flush.IsCompleted);
        ReadResult result = await pipe.Readers[1].ReadAsync();
        Assert.Equal([7, 8], result.Buffer.ToArray());
        pipe.Readers[1].AdvanceTo(result.Buffer.End);

        pipe.Writer.Complete();
        ReadResult completed = await pipe.Readers[1].ReadAsync();
        Assert.True(completed.IsCompleted);
        pipe.Readers[1].AdvanceTo(completed.Buffer.End);
        pipe.Readers[1].Complete();
        Assert.Same(expected, await pipe.ReaderCompletions[0]);
    }

    [Fact]
    public async Task CompletePipe_mode_allows_buffer_drain_then_throws_terminal_exception()
    {
        var options = new BroadcastPipeOptions(readerFailureBehavior: BroadcastPipeReaderFailureBehavior.CompletePipe);
        var pipe = new BroadcastPipe(2, options);
        await pipe.Writer.WriteAsync(new byte[] { 1, 2, 3 });
        var expected = new InvalidDataException("terminal");

        pipe.Readers[0].Complete(expected);

        ReadResult buffered = await pipe.Readers[1].ReadAsync();
        Assert.Equal([1, 2, 3], buffered.Buffer.ToArray());
        pipe.Readers[1].AdvanceTo(buffered.Buffer.End);

        InvalidDataException actual = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await pipe.Readers[1].ReadAsync());
        Assert.Same(expected, actual);
        Assert.Throws<InvalidOperationException>(() => pipe.Writer.GetMemory());
        pipe.Readers[1].Complete();
    }

    [Fact]
    public async Task CancelPendingRead_is_per_reader()
    {
        var pipe = new BroadcastPipe(2);
        ValueTask<ReadResult> firstPending = pipe.Readers[0].ReadAsync();
        ValueTask<ReadResult> secondPending = pipe.Readers[1].ReadAsync();

        pipe.Readers[0].CancelPendingRead();
        ReadResult canceled = await firstPending;
        Assert.True(canceled.IsCanceled);
        pipe.Readers[0].AdvanceTo(canceled.Buffer.Start, canceled.Buffer.End);
        Assert.False(secondPending.IsCompleted);

        await pipe.Writer.WriteAsync(new byte[] { 42 });
        ReadResult second = await secondPending;
        Assert.Equal([42], second.Buffer.ToArray());
        pipe.Readers[1].AdvanceTo(second.Buffer.End);

        ReadResult first = await pipe.Readers[0].ReadAsync();
        Assert.Equal([42], first.Buffer.ToArray());
        pipe.Readers[0].AdvanceTo(first.Buffer.End);
        Complete(pipe);
    }

    [Fact]
    public async Task CancelPendingFlush_is_global_and_does_not_discard_data()
    {
        var options = new BroadcastPipeOptions(pauseWriterThreshold: 4, resumeWriterThreshold: 2, useSynchronizationContext: false);
        var pipe = new BroadcastPipe(2, options);
        ValueTask<FlushResult> flush = pipe.Writer.WriteAsync(new byte[] { 1, 2, 3, 4 });
        Assert.False(flush.IsCompleted);

        pipe.Writer.CancelPendingFlush();
        FlushResult canceled = await flush;
        Assert.True(canceled.IsCanceled);

        foreach (PipeReader reader in pipe.Readers)
        {
            ReadResult result = await reader.ReadAsync();
            Assert.Equal([1, 2, 3, 4], result.Buffer.ToArray());
            reader.AdvanceTo(result.Buffer.End);
        }

        Complete(pipe);
    }

    [Fact]
    public async Task CompletePipe_uses_first_reader_fault_and_retains_each_completion_exception()
    {
        var pipe = new BroadcastPipe(
            3,
            new BroadcastPipeOptions(readerFailureBehavior: BroadcastPipeReaderFailureBehavior.CompletePipe));
        var first = new InvalidDataException("first");
        var second = new EndOfStreamException("second");

        pipe.Readers[0].Complete(first);
        pipe.Readers[1].Complete(second);

        InvalidDataException terminal = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await pipe.Readers[2].ReadAsync());
        Assert.Same(first, terminal);
        Assert.Same(first, await pipe.ReaderCompletions[0]);
        Assert.Same(second, await pipe.ReaderCompletions[1]);
        pipe.Readers[2].Complete();
        Assert.Null(await pipe.ReaderCompletions[2]);
    }

    [Fact]
    public async Task Reset_reuses_endpoints_and_replaces_completion_generation()
    {
        var pipe = new BroadcastPipe(2);
        PipeWriter writer = pipe.Writer;
        PipeReader firstReader = pipe.Readers[0];
        IReadOnlyList<Task<Exception?>> oldCompletions = pipe.ReaderCompletions;

        Complete(pipe);
        await Task.WhenAll(oldCompletions);
        pipe.Reset();

        Assert.Same(writer, pipe.Writer);
        Assert.Same(firstReader, pipe.Readers[0]);
        Assert.NotSame(oldCompletions, pipe.ReaderCompletions);
        Assert.All(pipe.ReaderCompletions, static task => Assert.False(task.IsCompleted));
        Assert.All(oldCompletions, static task => Assert.True(task.IsCompletedSuccessfully));

        await pipe.Writer.WriteAsync(new byte[] { 9 });
        ReadResult result = await firstReader.ReadAsync();
        Assert.Equal([9], result.Buffer.ToArray());
        firstReader.AdvanceTo(result.Buffer.End);
        Complete(pipe);
    }

    [Fact]
    public void Reset_rejects_incomplete_generation()
    {
        var pipe = new BroadcastPipe(1);
        Assert.Throws<InvalidOperationException>(() => pipe.Reset());
    }

    private static void Complete(BroadcastPipe pipe)
    {
        pipe.Writer.Complete();
        foreach (PipeReader reader in pipe.Readers)
        {
            reader.Complete();
        }
    }
}
