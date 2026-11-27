using System.Security.Cryptography;
using Azure.Storage.Blobs;
using TeeForge.Hashing;

namespace TeeForge.BlobUpload;

internal static class AzuriteSmoke
{
    internal static async Task RunAsync(CancellationToken cancellationToken)
    {
        // The smoke test always uses the loopback emulator and its public development account.
        var client = new BlobServiceClient("UseDevelopmentStorage=true",
            new BlobClientOptions(BlobClientOptions.ServiceVersion.V2023_11_03));
        BlobContainerClient container = client.GetBlobContainerClient($"teeforge-{Guid.NewGuid():N}");
        await container.CreateAsync(cancellationToken: cancellationToken);
        try
        {
            // Cross the SDK's block boundary before EOF or the injected fault.
            byte[] payload = new byte[5 * 1024 * 1024];
            new Random(731).NextBytes(payload);
            BlobClient blob = container.GetBlobClient("complete");
            using var input = new MemoryStream(payload, writable: false);
            await using var body = new HashingReadStream(input, TeeHashAlgorithm.SHA256, leaveOpen: true);
            await Upload.CopyAsync(body, blob, cancellationToken);
            var downloaded = await blob.DownloadContentAsync(cancellationToken);
            if (body.BytesRead != payload.Length ||
                !body.Results[TeeHashAlgorithm.SHA256].Bytes.Span.SequenceEqual(SHA256.HashData(payload)) ||
                !downloaded.Value.Content.ToMemory().Span.SequenceEqual(payload))
            {
                throw new InvalidDataException("Committed blob, byte count, or SHA-256 differs from the source.");
            }

            blob = container.GetBlobClient("interrupted");
            using var interrupted = new InterruptedInput(payload);
            await using var partial = new HashingReadStream(interrupted, TeeHashAlgorithm.SHA256, leaveOpen: true);
            try
            {
                await Upload.CopyAsync(partial, blob, cancellationToken);
                throw new InvalidOperationException("The interrupted upload returned success.");
            }
            catch (IOException error) when (ReferenceEquals(error, interrupted.Failure))
            {
                // The source failed after delivering the prefix and before its first EOF.
            }
            if (partial.BytesRead != payload.Length || partial.Results.IsComplete ||
                (await blob.ExistsAsync(cancellationToken)).Value)
            {
                throw new InvalidDataException("The interrupted upload reported an incorrect count, digest, or committed blob.");
            }
            Console.WriteLine("Verified uploaded bytes, SHA-256, and interruption before EOF.");
        }
        finally
        {
            await container.DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
        }
    }

    private sealed class InterruptedInput(byte[] prefix) : MemoryStream(prefix, writable: false)
    {
        internal IOException Failure { get; } = new("Injected upload source failure.");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position == Length && !buffer.IsEmpty
                ? ValueTask.FromException<int>(Failure)
                : base.ReadAsync(buffer, cancellationToken);
    }
}
