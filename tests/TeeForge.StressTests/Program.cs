using System.Buffers;
using System.IO.Pipelines;
using TeeForge;

int iterations = args.Length == 0 ? 25 : int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

for (int iteration = 0; iteration < iterations; iteration++)
{
    int readerCount = 2 + (iteration % 7);
    int payloadLength = 128 * 1024 + (iteration * 7919 % (384 * 1024));
    var random = new Random(0x5EED + iteration);
    byte[] payload = new byte[payloadLength];
    random.NextBytes(payload);

    var pipe = new TeePipe(
        readerCount,
        new TeePipeOptions(
            pauseWriterThreshold: 32 * 1024,
            resumeWriterThreshold: 16 * 1024,
            minimumSegmentSize: 1024,
            useSynchronizationContext: false));

    Task<byte[]>[] readers = Enumerable.Range(0, readerCount)
        .Select(index => ReadAllRandomlyAsync(pipe.Readers[index], payloadLength, 0xC0DE + iteration * 31 + index, timeout.Token))
        .ToArray();

    Task writer = WriteRandomlyAsync(pipe.Writer, payload, random, timeout.Token);
    await writer;
    byte[][] results = await Task.WhenAll(readers);

    for (int index = 0; index < results.Length; index++)
    {
        if (!payload.AsSpan().SequenceEqual(results[index]))
        {
            throw new InvalidDataException($"Iteration {iteration}, reader {index} did not receive the broadcast payload.");
        }
    }

    foreach (PipeReader reader in pipe.Readers)
    {
        reader.Complete();
    }
}

Console.WriteLine($"TeeForge stress test passed {iterations} deterministic randomized iterations.");

static async Task WriteRandomlyAsync(
    PipeWriter writer,
    ReadOnlyMemory<byte> payload,
    Random random,
    CancellationToken cancellationToken)
{
    int offset = 0;
    while (offset < payload.Length)
    {
        int count = Math.Min(payload.Length - offset, random.Next(1, 8193));
        if (random.Next(20) == 0)
        {
            writer.CancelPendingFlush();
        }

        FlushResult flush = await writer.WriteAsync(payload.Slice(offset, count), cancellationToken);
        if (flush.IsCompleted)
        {
            throw new InvalidOperationException("A reader completed before the stress payload was written.");
        }

        // CancelPendingFlush is advisory: the bytes were committed even when the flush reports cancellation.
        offset += count;
        if ((offset & 0x3FFF) == 0)
        {
            await Task.Yield();
        }
    }

    writer.Complete();
}

static async Task<byte[]> ReadAllRandomlyAsync(
    PipeReader reader,
    int expectedLength,
    int seed,
    CancellationToken cancellationToken)
{
    var random = new Random(seed);
    using var output = new MemoryStream(expectedLength);

    while (true)
    {
        if (random.Next(20) == 0)
        {
            reader.CancelPendingRead();
        }

        ReadResult result = await reader.ReadAsync(cancellationToken);
        ReadOnlySequence<byte> buffer = result.Buffer;
        if (result.IsCanceled)
        {
            reader.AdvanceTo(buffer.Start, buffer.End);
            continue;
        }

        if (!buffer.IsEmpty)
        {
            int consume = random.Next(1, checked((int)Math.Min(buffer.Length, 16 * 1024)) + 1);
            ReadOnlySequence<byte> consumed = buffer.Slice(0, consume);
            foreach (ReadOnlyMemory<byte> segment in consumed)
            {
                await output.WriteAsync(segment, cancellationToken);
            }

            SequencePosition consumedPosition = buffer.GetPosition(consume);
            reader.AdvanceTo(consumedPosition, buffer.End);
        }
        else
        {
            reader.AdvanceTo(buffer.End);
        }

        if (result.IsCompleted && buffer.IsEmpty)
        {
            break;
        }
    }

    return output.ToArray();
}
