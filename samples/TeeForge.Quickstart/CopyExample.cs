using TeeForge.Broadcasting;

namespace TeeForge.Quickstart;

internal static class CopyExample
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        byte[] payload = "Copy a C# stream to multiple destinations."u8.ToArray();
        await using var source = new MemoryStream(payload);
        await using var first = new MemoryStream();
        await using var second = new MemoryStream();

        // The collection selects TeeForge's extension rather than Stream's single-destination method.
        Stream[] destinations = [first, second];
        await source.CopyToAsync(destinations, cancellationToken);

        // CopyToAsync leaves caller streams open and does not flush them.
        await first.FlushAsync(cancellationToken);
        await second.FlushAsync(cancellationToken);
        if (!first.ToArray().AsSpan().SequenceEqual(payload) ||
            !second.ToArray().AsSpan().SequenceEqual(payload) || !source.CanRead)
        {
            throw new InvalidOperationException("Both destinations must receive the full payload, and the source must stay open.");
        }
    }
}
