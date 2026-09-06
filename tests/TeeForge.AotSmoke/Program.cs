using System.Buffers;
using System.IO.Hashing;
using System.IO.Pipelines;
using System.Security.Cryptography;
using TeeForge.Broadcasting;
using TeeForge.ErasureCoding;
using TeeForge.Hashing;
using TeeForge.Mirroring;

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

TeeHashResults hashes;
await using (var hashDestination = new MemoryStream())
{
    await using (var hashStream = new TeeHashStream(
        [TeeHashAlgorithm.SHA256, TeeHashAlgorithm.XxHash3],
        out hashes,
        [hashDestination],
        new TeeBufferedStreamOptions(leaveOpen: true)))
    {
        await hashStream.WriteAsync(payload);
    }

    if (!hashDestination.ToArray().AsSpan().SequenceEqual(payload))
    {
        return 2;
    }
}

if (!hashes.IsComplete ||
    !hashes[TeeHashAlgorithm.SHA256].Bytes.Span.SequenceEqual(SHA256.HashData(payload)) ||
    !hashes[TeeHashAlgorithm.XxHash3].Bytes.Span.SequenceEqual(XxHash3.Hash(payload)))
{
    return 3;
}

var pipe = new BroadcastPipe(2);
await pipe.Writer.WriteAsync(payload);
pipe.Writer.Complete();

foreach (PipeReader reader in pipe.Readers)
{
    ReadResult result = await reader.ReadAsync();
    if (!result.Buffer.ToArray().AsSpan().SequenceEqual(payload))
    {
        return 4;
    }

    reader.AdvanceTo(result.Buffer.End);
    reader.Complete();
}

MemoryStream[] members = Enumerable.Range(0, 6).Select(_ => new MemoryStream()).ToArray();
try
{
    var options = new ErasureStreamOptions(leaveOpen: true, readAheadBlockCount: 0);
    await using (ErasureStream encoded = ErasureStream.Create(members, 4, 2, payload.Length, 4096, options))
    {
        await encoded.WriteAsync(payload);
        await encoded.CompleteAsync();
    }

    Stream?[] surviving = [null, members[1], members[2], members[3], null, members[5]];
    await using ErasureStream decoded = ErasureStream.Open(surviving, 4, 2, payload.Length, 4096,
        new ErasureStreamOptions(requireAllMembers: false, leaveOpen: true, readAheadBlockCount: 0));
    byte[] actual = new byte[payload.Length];
    await decoded.ReadExactlyAsync(actual);
    if (!actual.AsSpan().SequenceEqual(payload)) return 5;
}
finally
{
    foreach (MemoryStream member in members) await member.DisposeAsync();
}

await using (var source = new MemoryStream(payload))
await using (var broadcast = new BroadcastHashStream(
    [TeeHashAlgorithm.SHA256, TeeHashAlgorithm.XxHash3],
    out TeeHashResults broadcastHashes,
    source, 2,
    new BroadcastStreamOptions(bufferSize: 2, pauseWriterThreshold: 2, resumeWriterThreshold: 1)))
{
    await using var first = new MemoryStream();
    await using var second = new MemoryStream();
    await Task.WhenAll(broadcast.Readers[0].CopyToAsync(first), broadcast.Readers[1].CopyToAsync(second));
    await broadcast.Completion;
    if (!first.ToArray().AsSpan().SequenceEqual(payload) || !second.ToArray().AsSpan().SequenceEqual(payload)
        || !broadcastHashes[TeeHashAlgorithm.SHA256].Bytes.Span.SequenceEqual(SHA256.HashData(payload))
        || !broadcastHashes[TeeHashAlgorithm.XxHash3].Bytes.Span.SequenceEqual(XxHash3.Hash(payload)))
    {
        return 6;
    }
}

await using (var source = new MemoryStream(payload))
await using (var first = new MemoryStream())
await using (var second = new MemoryStream())
{
    await source.CopyToAsync([first, second],
        new BroadcastCopyOptions(bufferSize: 2, pauseWriterThreshold: 2, resumeWriterThreshold: 1));
    if (!first.ToArray().AsSpan().SequenceEqual(payload) || !second.ToArray().AsSpan().SequenceEqual(payload))
    {
        return 7;
    }
}

await using (var source = new MemoryStream(payload))
await using (var first = new MemoryStream())
await using (var second = new MemoryStream())
{
    var copyHashes = await source.CopyToAsync(
        new[] { TeeHashAlgorithm.SHA256, TeeHashAlgorithm.XxHash3 }, first, second);
    if (!first.ToArray().AsSpan().SequenceEqual(payload) || !second.ToArray().AsSpan().SequenceEqual(payload)
        || !copyHashes[TeeHashAlgorithm.SHA256].Bytes.Span.SequenceEqual(SHA256.HashData(payload))
        || !copyHashes[TeeHashAlgorithm.XxHash3].Bytes.Span.SequenceEqual(XxHash3.Hash(payload)))
    {
        return 8;
    }
}

return 0;
