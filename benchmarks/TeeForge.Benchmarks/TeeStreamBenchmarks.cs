using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TeeForge.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 3, iterationCount: 8)]
public class TeeStreamBenchmarks
{
    private byte[] _payload = null!;

    [Params(4 * 1024, 64 * 1024, 1024 * 1024)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup() => _payload = GC.AllocateUninitializedArray<byte>(PayloadSize);

    [Benchmark(Baseline = true)]
    public void ManualSequentialLoop()
    {
        using var first = new MemoryStream(PayloadSize);
        using var second = new MemoryStream(PayloadSize);
        first.Write(_payload);
        second.Write(_payload);
    }

    [Benchmark]
    public void TeeStreamSequential()
    {
        using var first = new MemoryStream(PayloadSize);
        using var second = new MemoryStream(PayloadSize);
        using var tee = new TeeStream(new TeeStreamOptions(leaveOpen: true), first, second);
        tee.Write(_payload);
    }
}
