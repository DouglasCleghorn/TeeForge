using System.Security.Cryptography;
using TeeForge.Broadcasting;
using TeeForge.Hashing;

namespace TeeForge.Quickstart;

internal static class HashExample
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        byte[] payload = "Calculate multiple hashes while copying a stream."u8.ToArray();
        await using var source = new MemoryStream(payload);
        await using var destination = new MemoryStream();

        TeeHashResults hashes = await source.CopyToAsync(
            [TeeHashAlgorithm.SHA256, TeeHashAlgorithm.XxHash3],
            destination,
            cancellationToken: cancellationToken);

        // Both key forms identify the same SHA-256 result. HashAlgorithmName inputs are also supported.
        TeeHashResult sha256 = hashes[HashAlgorithmName.SHA256];
        Console.WriteLine($"SHA-256: {sha256.Hex}");
        Console.WriteLine($"XXH3: {hashes[TeeHashAlgorithm.XxHash3].Hex}");

        if (!hashes.IsComplete || !ReferenceEquals(sha256, hashes[TeeHashAlgorithm.SHA256]) ||
            !sha256.Bytes.Span.SequenceEqual(SHA256.HashData(payload)) ||
            !destination.ToArray().AsSpan().SequenceEqual(payload))
        {
            throw new InvalidOperationException("Completed hashes must describe the copied source bytes.");
        }
    }
}
