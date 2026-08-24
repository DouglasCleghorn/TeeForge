using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using TeeForge.Hashing.Internal;

namespace TeeForge.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 3, iterationCount: 8)]
public class HashStreamApiBenchmarks : IDisposable
{
    private const int ReadBufferSize = 81920;

    private MemoryStream _computeHashStream = null!;
    private MemoryStream _hashDataStream = null!;
    private MemoryStream _hashWriteStreamSource = null!;

    [Params(4 * 1024, 64 * 1024, 1024 * 1024)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        byte[] payload = GC.AllocateUninitializedArray<byte>(PayloadSize);
        Random.Shared.NextBytes(payload);

        _hashWriteStreamSource = new MemoryStream(payload, writable: false);
        _hashDataStream = new MemoryStream(payload, writable: false);
        _computeHashStream = new MemoryStream(payload, writable: false);

        VerifyImplementations(payload);
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    [Benchmark(Baseline = true)]
    public byte[] HashWriteStreamCopyTo()
    {
        _hashWriteStreamSource.Position = 0;
        using var destination = new HashWriteStream(HashAlgorithmName.SHA256);
        using var bufferedDestination = new BufferedStream(destination);
        _hashWriteStreamSource.CopyTo(bufferedDestination, ReadBufferSize);
        bufferedDestination.Flush();
        return destination.GetHashAndReset();
    }

    [Benchmark]
    public byte[] Sha256HashDataStream()
    {
        _hashDataStream.Position = 0;
        return SHA256.HashData(_hashDataStream);
    }

    [Benchmark]
    public byte[] Sha256CreateComputeHashStream()
    {
        _computeHashStream.Position = 0;
        using SHA256 hash = SHA256.Create();
        return hash.ComputeHash(_computeHashStream);
    }

    public void Dispose()
    {
        _hashWriteStreamSource?.Dispose();
        _hashDataStream?.Dispose();
        _computeHashStream?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void VerifyImplementations(byte[] payload)
    {
        byte[] expected = SHA256.HashData(payload);
        byte[] hashWriteStream = HashWriteStreamCopyTo();
        byte[] hashData = Sha256HashDataStream();
        byte[] computeHash = Sha256CreateComputeHashStream();

        if (!expected.AsSpan().SequenceEqual(hashWriteStream) ||
            !expected.AsSpan().SequenceEqual(hashData) ||
            !expected.AsSpan().SequenceEqual(computeHash))
        {
            throw new InvalidOperationException("The SHA-256 benchmark implementations returned different hashes.");
        }
    }
}
