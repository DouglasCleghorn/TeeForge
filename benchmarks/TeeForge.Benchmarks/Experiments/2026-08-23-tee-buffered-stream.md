# TeeBufferedStream shared-buffer crossover

Status: completed on 2026-08-23.

Source state: commit `bbea61da1e8c1d3af66bffe6d805f558cdc13701`
plus the uncommitted `TeeBufferedStream`, `TeeStream` sequential byte-array
write optimization, tests, documentation, and benchmark harness.
Raw BenchmarkDotNet artifacts are ignored and are not part of the retained
record.

## Purpose and methodology

This experiment measures the cost of writing one 64 KiB payload to two
destinations through `TeeStream` and through `TeeBufferedStream`. Each logical
operation writes the payload in fixed-size chunks and then flushes. The sink
streams observe each call without copying the payload, keeping the result
focused on TeeForge dispatch and buffering overhead.

`TeeBufferedStream` uses its default 4 KiB shared buffer. Its buffering engine
is adapted from the .NET 10 `BufferedStream` source, including the large-write
bypass path. Both TeeForge streams use sequential synchronous fan-out and keep
their two destinations open between benchmark invocations.

## Environment

- BenchmarkDotNet 0.15.8
- Windows 11 build 26200.9168
- AMD Ryzen 9 5900X, 12 physical / 24 logical cores
- .NET SDK 10.0.400; .NET runtime 10.0.11 x64 RyuJIT
- Concurrent workstation GC; BenchmarkDotNet high-performance power plan
- 3 warmup and 8 measurement iterations per case

Command:

```text
dotnet run -c Release --no-restore --project benchmarks/TeeForge.Benchmarks -- --filter '*TeeBufferedStreamBenchmarks*'
```

## Retained results

| Write size | Stream | Mean | Error | Allocated | Ratio to TeeStream |
| ---: | --- | ---: | ---: | ---: | ---: |
| 64 B | `TeeStream` | 16,294.29 ns | 904.699 ns | 24 B | 1.00x |
| 64 B | `TeeBufferedStream` | 7,869.36 ns | 839.336 ns | 24 B | 0.48x |
| 256 B | `TeeStream` | 3,839.47 ns | 293.256 ns | 26 B | 1.00x |
| 256 B | `TeeBufferedStream` | 1,819.09 ns | 51.198 ns | 24 B | 0.47x |
| 1 KiB | `TeeStream` | 788.54 ns | 12.307 ns | 24 B | 1.00x |
| 1 KiB | `TeeBufferedStream` | 906.80 ns | 18.597 ns | 24 B | 1.15x |
| 4 KiB | `TeeStream` | 260.79 ns | 16.953 ns | 24 B | 1.00x |
| 4 KiB | `TeeBufferedStream` | 285.47 ns | 14.749 ns | 24 B | 1.10x |
| 16 KiB | `TeeStream` | 75.16 ns | 8.033 ns | 24 B | 1.00x |
| 16 KiB | `TeeBufferedStream` | 64.44 ns | 1.247 ns | 24 B | 0.86x |

## Conclusion

The shared buffer materially improves tiny writes in this synthetic workload:
64-byte and 256-byte cases complete in 48% and 47% of the direct `TeeStream`
time because many logical writes become one mirrored emission. At 1 KiB and
4 KiB the buffering bookkeeping costs 15% and 10%, respectively. The 16 KiB
case takes the inherited large-write bypass and did not regress in this run.

The benchmark originally exposed closures in `TeeStream.Write(byte[], int,
int)` that allocated 1,560 B and then 536 B per operation. Moving the sequential
fan-out into an explicit all-destination loop and isolating the concurrent-only
closure reduced the accepted implementation to 24 B for all
`TeeBufferedStream` cases. The 26 B displayed for the 256-byte baseline is
BenchmarkDotNet's averaged accounting, not a distinct allocation in the code.

These in-memory sinks expose per-call overhead; they do not predict storage or
network throughput. The retained harness is the reproducible crossover test.
