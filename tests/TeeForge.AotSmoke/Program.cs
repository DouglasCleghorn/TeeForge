using System.Buffers;
using System.IO.Hashing;
using System.IO.Pipelines;
using System.Security.Cryptography;
using TeeForge.Hashing;
using TeeForge.Mirroring;
using TeeForge.Pipelines;
using TeeForge.Sparse;

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

TeeHashResults<TeeHashAlgorithm> hashes;
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

var pipe = new TeePipe(2);
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

var dynamicOptions = new DynamicAllocationStreamOptions(
    leaveOpen: true,
    freeBlockQueueCapacity: 0,
    freeBlockQueueLowWatermark: 0);
await using var dynamicBacking = new MemoryStream();
await using (DynamicAllocationStream created = await DynamicAllocationStream.CreateAsync(
    dynamicBacking,
    16L * 64 * 1024,
    64 * 1024,
    dynamicOptions))
{
    created.Position = (2L * 64 * 1024) + 7;
    await created.WriteAsync(payload);
    await created.FlushAsync();
}

dynamicBacking.Position = 0;
await using (DynamicAllocationStream opened = await DynamicAllocationStream.OpenAsync(dynamicBacking, dynamicOptions))
{
    if (opened.Length != 3L * 64 * 1024)
    {
        return 5;
    }

    opened.Position = (2L * 64 * 1024) + 7;
    byte[] dynamicResult = new byte[payload.Length];
    await opened.ReadExactlyAsync(dynamicResult);
    if (!dynamicResult.AsSpan().SequenceEqual(payload))
    {
        return 6;
    }

    var differenceOptions = new DifferencingStreamOptions(
        leaveBaseOpen: true,
        leaveDifferenceOpen: true);
    await using var differenceBacking = new MemoryStream();
    await using (DifferencingStream child = await DifferencingStream.CreateAsync(
        opened,
        differenceBacking,
        differenceOptions,
        "base.tfdisk"))
    {
        await child.WriteAtAsync(new byte[] { 9, 8 }, 4095);
        byte[] differenceResult = new byte[2];
        if (await child.ReadAtAsync(differenceResult, 4095) != 2 ||
            !differenceResult.AsSpan().SequenceEqual(new byte[] { 9, 8 }))
        {
            return 7;
        }

        DifferencingStreamLocator locator = await DifferencingStream.ReadLocatorAsync(differenceBacking);
        if (locator.BaseId != opened.Id || locator.ParentPathHint != "base.tfdisk")
        {
            return 8;
        }
    }
}

return 0;
