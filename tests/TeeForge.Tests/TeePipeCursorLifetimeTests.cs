using System.IO.Pipelines;

namespace TeeForge.Tests;

public class TeePipeCursorLifetimeTests
{
    [Fact]
    public async Task Empty_outstanding_read_keeps_its_sequence_position_valid()
    {
        var pipe = new TeePipe(2, new TeePipeOptions(minimumSegmentSize: 16, useSynchronizationContext: false));
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
