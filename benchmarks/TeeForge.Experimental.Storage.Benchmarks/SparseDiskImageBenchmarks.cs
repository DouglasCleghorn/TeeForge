using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TeeForge.Experimental.Storage.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 3, iterationCount: 8)]
public class SparseDiskImageBenchmarks : IDisposable
{
    private const int BlockSize = 64 * 1024;
    private const long VirtualCapacity = 2048L * BlockSize;
    private readonly SparseDiskImageOptions _options = new(
        leaveOpen: true,
        freeBlockQueueCapacity: 0,
        freeBlockQueueLowWatermark: 0);

    private byte[] _payload = null!;
    private MemoryStream _plain = null!;
    private MemoryStream _backing = null!;
    private SparseDiskImage _sparse = null!;

    [Params(4 * 1024, 64 * 1024)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _payload = GC.AllocateUninitializedArray<byte>(PayloadSize);
        _plain = new MemoryStream(new byte[BlockSize], writable: true);
        _backing = new MemoryStream();
        _sparse = SparseDiskImage.Create(_backing, VirtualCapacity, BlockSize, _options);
        _sparse.Write(new byte[BlockSize]);
        _sparse.Flush();
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _sparse?.Dispose();
        _backing?.Dispose();
        _plain?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark(Baseline = true)]
    public void MemoryStreamAllocatedOverwrite()
    {
        _plain.Position = 0;
        _plain.Write(_payload);
    }

    [Benchmark]
    public void SparseDiskImageAllocatedOverwrite()
    {
        _sparse.Position = 0;
        _sparse.Write(_payload);
    }

    [Benchmark]
    public void SparseDiskImageSparseFirstWrite()
    {
        using var backing = new MemoryStream();
        using SparseDiskImage sparse = SparseDiskImage.Create(backing, VirtualCapacity, BlockSize, _options);
        sparse.Position = 1024L * BlockSize;
        sparse.Write(_payload);
    }
}
