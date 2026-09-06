using TeeForge.Broadcasting;

namespace TeeForge.Quickstart;

internal static class BroadcastExample
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        // Exceed the buffer threshold so this example exercises slow-reader backpressure.
        byte[] payload = Enumerable.Range(0, 100_003).Select(value => (byte)value).ToArray();
        await using var source = new MemoryStream(payload);
        await using var first = new MemoryStream();
        await using var second = new MemoryStream();
        await using var broadcast = new BroadcastStream(source, readerCount: 2,
            new BroadcastStreamOptions(leaveOpen: true), cancellationToken);

        // Start every consumer before awaiting completion. An idle reader can block the producer.
        Task firstCopy = broadcast.Readers[0].CopyToAsync(first, cancellationToken);
        Task secondCopy = broadcast.Readers[1].CopyToAsync(second, cancellationToken);
        await Task.WhenAll(firstCopy, secondCopy);
        await broadcast.Completion;

        if (!first.ToArray().AsSpan().SequenceEqual(payload) ||
            !second.ToArray().AsSpan().SequenceEqual(payload))
        {
            throw new InvalidOperationException("Every independent reader must receive the entire sequence.");
        }
    }
}
