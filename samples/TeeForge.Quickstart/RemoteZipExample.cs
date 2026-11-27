using System.IO.Compression;
using TeeForge.RandomAccess;

namespace TeeForge.Quickstart;

internal static class RemoteZipExample
{
    public static async Task CopyEntryAsync(
        Stream source, string entryName, Stream destination, CancellationToken cancellationToken)
    {
        // A small window limits bytes reserved for scattered metadata. Larger windows suit long entries.
        await using var buffered = new ReadAheadStream(source, windowSize: 16 * 1024, leaveOpen: true);
        await using var zip = await ZipArchive.CreateAsync(
            buffered, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: null, cancellationToken);
        ZipArchiveEntry entry = zip.GetEntry(entryName)
            ?? throw new FileNotFoundException($"The archive does not contain '{entryName}'.", entryName);
        await using Stream content = await entry.OpenAsync(cancellationToken);
        await content.CopyToAsync(destination, cancellationToken);
    }

    // Pass an HTTPS URL (including a signed object URL) that supports byte ranges.
    public static async Task RunRemoteAsync(
        HttpClient client, Uri url, string entryName, Stream destination, CancellationToken cancellationToken)
    {
        await using var source = await HttpRandomAccessStream.OpenAsync(client, url, cancellationToken: cancellationToken);
        await CopyEntryAsync(source, entryName, destination, cancellationToken);
    }

    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        // Run the same archive reader against an in-memory fixture in the offline quickstart.
        await using var source = new RandomAccessMemoryStream();
        using (var zip = new ZipArchive(source, ZipArchiveMode.Create, leaveOpen: true))
        {
            using Stream manifest = zip.CreateEntry("manifest.txt").Open();
            manifest.Write("version=1"u8);
        }

        await using var destination = new MemoryStream();
        await CopyEntryAsync(source, "manifest.txt", destination, cancellationToken);
        if (!destination.ToArray().AsSpan().SequenceEqual("version=1"u8))
        {
            throw new InvalidOperationException("The archive entry contents changed.");
        }
    }
}
