using System.IO.Pipelines;

namespace TeeForge.Tests;

public class BroadcastPipeCursorLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reserved_unpublished_successor_keeps_empty_reader_cursors_valid(bool completeWriter)
    {
        var pipe = new BroadcastPipe(2, new BroadcastPipeOptions(minimumSegmentSize: 16, useSynchronizationContext: false));
        await pipe.Writer.WriteAsync(new byte[] { 1, 2, 3 });
        _ = pipe.Writer.GetMemory(32);
        if (completeWriter)
        {
            pipe.Writer.Complete();
        }

        ReadResult first = await pipe.Readers[0].ReadAsync();
        pipe.Readers[0].AdvanceTo(first.Buffer.End);
        if (!completeWriter)
        {
            pipe.Readers[0].CancelPendingRead();
        }

        ReadResult empty = await pipe.Readers[0].ReadAsync();
        Assert.True(empty.Buffer.IsEmpty);
        ReadResult second = await pipe.Readers[1].ReadAsync();
        pipe.Readers[1].AdvanceTo(second.Buffer.End);
        pipe.Readers[0].AdvanceTo(empty.Buffer.End);

        if (!completeWriter)
        {
            pipe.Writer.GetSpan(1)[0] = 4;
            pipe.Writer.Advance(1);
            await pipe.Writer.FlushAsync();
        }

        pipe.Writer.Complete();
        foreach (System.IO.Pipelines.PipeReader reader in pipe.Readers)
        {
            ReadResult tail = await reader.ReadAsync();
            Assert.Equal(completeWriter ? 0 : 1, tail.Buffer.Length);
            reader.AdvanceTo(tail.Buffer.End);
            reader.Complete();
        }
    }

    [Fact]
    public async Task Empty_outstanding_read_keeps_its_sequence_position_valid()
    {
        var pipe = new BroadcastPipe(2, new BroadcastPipeOptions(minimumSegmentSize: 16, useSynchronizationContext: false));
        await pipe.Writer.WriteAsync(new byte[16]);

        ReadResult firstPayload = await pipe.Readers[0].ReadAsync();
        pipe.Readers[0].AdvanceTo(firstPayload.Buffer.End);
        ReadResult secondPayload = await pipe.Readers[1].ReadAsync();
        pipe.Readers[1].AdvanceTo(secondPayload.Buffer.End);

        pipe.Writer.Complete();
        ReadResult emptyFirst = await pipe.Readers[0].ReadAsync();
        Assert.True(emptyFirst.Buffer.IsEmpty);

        ReadResult emptySecond = await pipe.Readers[1].ReadAsync();
        pipe.Readers[1].AdvanceTo(emptySecond.Buffer.End);

        pipe.Readers[0].AdvanceTo(emptyFirst.Buffer.End);
        pipe.Readers[0].Complete();
        pipe.Readers[1].Complete();
    }
}
