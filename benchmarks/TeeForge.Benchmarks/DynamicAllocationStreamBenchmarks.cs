using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TeeForge.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 3, iterationCount: 8)]
public class DynamicAllocationStreamBenchmarks : IDisposable
{
    private const int BlockSize = 64 * 1024;
    private readonly DynamicAllocationStreamOptions _options = new(
        leaveOpen: true,
        freeBlockQueueCapacity: 0,
        freeBlockQueueLowWatermark: 0);

    private byte[] _payload = null!;
    private MemoryStream _plain = null!;
    private MemoryStream _backing = null!;
    private DynamicAllocationStream _sparse = null!;

    [Params(4 * 1024, 64 * 1024)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _payload = GC.AllocateUninitializedArray<byte>(PayloadSize);
        _plain = new MemoryStream(new byte[BlockSize], writable: true);
        _backing = new MemoryStream();
        _sparse = DynamicAllocationStream.Create(_backing, BlockSize, _options);
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
    public void DynamicAllocationStreamAllocatedOverwrite()
    {
        _sparse.Position = 0;
        _sparse.Write(_payload);
    }

    [Benchmark]
    public void DynamicAllocationStreamSparseFirstWrite()
    {
        using var backing = new MemoryStream();
        using DynamicAllocationStream sparse = DynamicAllocationStream.Create(backing, BlockSize, _options);
        sparse.Position = 1024L * BlockSize;
        sparse.Write(_payload);
    }
}
