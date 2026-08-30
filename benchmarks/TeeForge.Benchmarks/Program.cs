using BenchmarkDotNet.Running;

namespace TeeForge.Benchmarks;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Contains("--erasure-stream-files", StringComparer.OrdinalIgnoreCase))
        {
            await ErasureStreamFileExperiment.RunAsync(args).ConfigureAwait(false);
            return;
        }

        if (args.Contains("--erasure-stream-memory", StringComparer.OrdinalIgnoreCase))
        {
            await ErasureStreamFileExperiment.RunMemoryAsync(args).ConfigureAwait(false);
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
