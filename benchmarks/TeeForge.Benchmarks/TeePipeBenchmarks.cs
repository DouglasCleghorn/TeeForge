using System.Buffers;
using System.IO.Pipelines;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TeeForge.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 3, iterationCount: 8)]
public class TeePipeBenchmarks
{
    private byte[] _payload = null!;

    [Params(4 * 1024, 64 * 1024, 1024 * 1024)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup() => _payload = GC.AllocateUninitializedArray<byte>(PayloadSize);

    [Benchmark(Baseline = true)]
    public async Task MicrosoftPipeOneReader()
    {
        var pipe = new Pipe(new PipeOptions(useSynchronizationContext: false));
        Task drain = DrainAsync(pipe.Reader);
        await pipe.Writer.WriteAsync(_payload);
        pipe.Writer.Complete();
        await drain;
    }

    [Benchmark]
    public async Task TeePipeOneReader()
    {
        var pipe = new TeePipe(1, new TeePipeOptions(useSynchronizationContext: false));
        Task drain = DrainAsync(pipe.Readers[0]);
        await pipe.Writer.WriteAsync(_payload);
        pipe.Writer.Complete();
        await drain;
    }

    [Benchmark]
    public async Task IndependentMicrosoftPipesFourReaders()
    {
        Pipe[] pipes = Enumerable.Range(0, 4)
            .Select(static _ => new Pipe(new PipeOptions(useSynchronizationContext: false)))
            .ToArray();

        Task[] drains = pipes.Select(static pipe => DrainAsync(pipe.Reader)).ToArray();
        await Task.WhenAll(pipes.Select(pipe => pipe.Writer.WriteAsync(_payload).AsTask()));
        foreach (Pipe pipe in pipes)
        {
            pipe.Writer.Complete();
        }

        await Task.WhenAll(drains);
    }

    [Benchmark]
    public async Task TeePipeFourReaders()
    {
        var pipe = new TeePipe(4, new TeePipeOptions(useSynchronizationContext: false));
        Task[] drains = pipe.Readers.Select(DrainAsync).ToArray();
        await pipe.Writer.WriteAsync(_payload);
        pipe.Writer.Complete();
        await Task.WhenAll(drains);
    }

    private static async Task DrainAsync(PipeReader reader)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync();
            ReadOnlySequence<byte> buffer = result.Buffer;
            reader.AdvanceTo(buffer.End);
            if (result.IsCompleted)
            {
                reader.Complete();
                return;
            }
        }
    }
}
