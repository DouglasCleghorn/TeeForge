using System.Buffers;
using System.IO.Pipelines;
using TeeForge;

byte[] payload = [1, 2, 3, 4];

await using (var first = new MemoryStream())
await using (var second = new MemoryStream())
await using (var tee = new TeeStream(new TeeStreamOptions(leaveOpen: true), first, second))
{
    await tee.WriteAsync(payload);
    await tee.FlushAsync();
    if (!first.ToArray().AsSpan().SequenceEqual(payload) || !second.ToArray().AsSpan().SequenceEqual(payload))
    {
        return 1;
    }
}

var pipe = new TeePipe(2);
await pipe.Writer.WriteAsync(payload);
pipe.Writer.Complete();

foreach (PipeReader reader in pipe.Readers)
{
    ReadResult result = await reader.ReadAsync();
    if (!result.Buffer.ToArray().AsSpan().SequenceEqual(payload))
    {
        return 2;
    }

    reader.AdvanceTo(result.Buffer.End);
    reader.Complete();
}

return 0;
