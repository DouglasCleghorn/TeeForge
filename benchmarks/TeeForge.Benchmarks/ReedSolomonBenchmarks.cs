using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using TeeForge.ErasureCoding.Internal;

namespace TeeForge.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 3, iterationCount: 8)]
public class ReedSolomonBenchmarks
{
    private ReedSolomonCodec _accelerated = null!;
    private byte[][] _acceleratedShards = null!;
    private ReedSolomonCodec _scalar = null!;
    private byte[][] _scalarShards = null!;

    [Params(64 * 1024, 1024 * 1024)]
    public int ShardSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        const int dataCount = 6;
        const int parityCount = 2;
        var random = new Random(0x5EED);
        _scalarShards = CreateShards(dataCount, parityCount, ShardSize, random);
        _acceleratedShards = _scalarShards.Select(static shard => shard.ToArray()).ToArray();
        _scalar = new ReedSolomonCodec(dataCount, parityCount, ReedSolomonAcceleration.Scalar);
        _accelerated = new ReedSolomonCodec(dataCount, parityCount);
    }

    [Benchmark(Baseline = true)]
    public void Scalar() => _scalar.Encode(_scalarShards, 0, ShardSize);

    [Benchmark]
    public void HardwareAccelerated() => _accelerated.Encode(_acceleratedShards, 0, ShardSize);

    private static byte[][] CreateShards(int dataCount, int parityCount, int shardSize, Random random)
    {
        var shards = new byte[dataCount + parityCount][];
        for (int member = 0; member < shards.Length; member++)
        {
            shards[member] = GC.AllocateUninitializedArray<byte>(shardSize);
            if (member < dataCount)
            {
                random.NextBytes(shards[member]);
            }
        }

        return shards;
    }
}
