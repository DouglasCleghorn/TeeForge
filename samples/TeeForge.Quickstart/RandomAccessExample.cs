using TeeForge.RandomAccess;

namespace TeeForge.Quickstart;

internal static class RandomAccessExample
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var source = new RandomAccessMemoryStream("0123456789"u8.ToArray());
        source.Position = 7;
        byte[] bytes = new byte[3];

        int read = await source.ReadAtAsync(bytes, offset: 2, cancellationToken);
        if (read != 3 || !bytes.AsSpan().SequenceEqual("234"u8) || source.Position != 7)
        {
            throw new InvalidOperationException("An explicit-offset read must preserve Position.");
        }

        // The range has its own cursor and ends at the requested length.
        await using Stream range = await source.OpenReadRangeAsync(offset: 4, length: 2, cancellationToken);
        await using var destination = new MemoryStream();
        await range.CopyToAsync(destination, cancellationToken);
        if (!destination.ToArray().AsSpan().SequenceEqual("45"u8) || source.Position != 7)
        {
            throw new InvalidOperationException("Range reads must be bounded and independent of Position.");
        }
    }
}
