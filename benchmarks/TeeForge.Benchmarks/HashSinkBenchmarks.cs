using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using TeeForge.Hashing.Internal;

namespace TeeForge.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 3, iterationCount: 8)]
public class HashSinkWriteBenchmarks : IDisposable
{
    private CryptoStream _cryptoAsync = null!;
    private SHA256 _cryptoAsyncAlgorithm = null!;
    private CryptoStream _cryptoSync = null!;
    private SHA256 _cryptoSyncAlgorithm = null!;
    private BufferedStream _incrementalAsync = null!;
    private HashWriteStream _incrementalAsyncHash = null!;
    private BufferedStream _incrementalSync = null!;
    private HashWriteStream _incrementalSyncHash = null!;
    private byte[] _payload = null!;

    [Params(4 * 1024, 64 * 1024, 1024 * 1024)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _payload = GC.AllocateUninitializedArray<byte>(PayloadSize);
        Random.Shared.NextBytes(_payload);

        _incrementalSyncHash = new HashWriteStream(HashAlgorithmName.SHA256);
        _incrementalSync = new BufferedStream(_incrementalSyncHash);
        _incrementalAsyncHash = new HashWriteStream(HashAlgorithmName.SHA256);
        _incrementalAsync = new BufferedStream(_incrementalAsyncHash);

        _cryptoSyncAlgorithm = SHA256.Create();
        _cryptoSync = new CryptoStream(Stream.Null, _cryptoSyncAlgorithm, CryptoStreamMode.Write, leaveOpen: true);

        _cryptoAsyncAlgorithm = SHA256.Create();
        _cryptoAsync = new CryptoStream(Stream.Null, _cryptoAsyncAlgorithm, CryptoStreamMode.Write, leaveOpen: true);
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        DisposeCryptoStream(_cryptoSync, _cryptoSyncAlgorithm);
        DisposeCryptoStream(_cryptoAsync, _cryptoAsyncAlgorithm);
        DisposeHashWriteStream(_incrementalSync, _incrementalSyncHash);
        DisposeHashWriteStream(_incrementalAsync, _incrementalAsyncHash);
        GC.SuppressFinalize(this);
    }

    [Benchmark(Baseline = true)]
    public void IncrementalHashWrite() => _incrementalSync.Write(_payload);

    [Benchmark]
    public void CryptoStreamWrite() => _cryptoSync.Write(_payload);

    [Benchmark]
    public ValueTask IncrementalHashWriteAsync() => _incrementalAsync.WriteAsync(_payload);

    [Benchmark]
    public ValueTask CryptoStreamWriteAsync() => _cryptoAsync.WriteAsync(_payload);

    private static void DisposeCryptoStream(CryptoStream? stream, HashAlgorithm? algorithm)
    {
        if (stream is not null)
        {
            if (!stream.HasFlushedFinalBlock)
            {
                stream.FlushFinalBlock();
            }

            GC.KeepAlive(algorithm?.Hash);
            stream.Dispose();
        }

        algorithm?.Dispose();
    }

    private static void DisposeHashWriteStream(BufferedStream? stream, HashWriteStream? hashStream)
    {
        if (stream is null)
        {
            hashStream?.Dispose();
            return;
        }

        stream.Flush();
        GC.KeepAlive(hashStream?.GetHashAndReset());
        stream.Dispose();
    }
}

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 3, iterationCount: 8)]
public class HashSinkLifecycleBenchmarks
{
    private byte[] _payload = null!;

    [Params(4 * 1024, 64 * 1024, 1024 * 1024)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _payload = GC.AllocateUninitializedArray<byte>(PayloadSize);
        Random.Shared.NextBytes(_payload);
    }

    [Benchmark(Baseline = true)]
    public byte[] IncrementalHashLifecycle()
    {
        using var hashStream = new HashWriteStream(HashAlgorithmName.SHA256);
        using var stream = new BufferedStream(hashStream);
        stream.Write(_payload);
        stream.Flush();
        return hashStream.GetHashAndReset();
    }

    [Benchmark]
    public byte[] CryptoStreamLifecycle()
    {
        using SHA256 algorithm = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, algorithm, CryptoStreamMode.Write, leaveOpen: true);
        stream.Write(_payload);
        stream.FlushFinalBlock();
        return algorithm.Hash!;
    }
}
