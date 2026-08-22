using BenchmarkDotNet.Running;

namespace TeeForge.Benchmarks;

public static class Program
{
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
