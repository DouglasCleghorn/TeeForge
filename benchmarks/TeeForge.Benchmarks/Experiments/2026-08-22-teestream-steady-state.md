# TeeStream steady-state write experiment

Status: completed on 2026-08-22 after two rejected harness revisions.

Source state: the initial signed repository commit containing this record. The
working tree was clean for the retained run except for ignored BenchmarkDotNet
artifacts.

## Purpose and harness corrections

The construction-inclusive baseline cannot isolate the write path. This
experiment preallocates two fixed-buffer `Stream` destinations for each path and
performs the same indexed virtual-call loop manually and through `TeeStream`.
The destination write is non-inlineable and publishes a volatile checksum so
neither the JIT nor dead-store elimination can discard the copy.

Two earlier runs are rejected rather than reported as performance evidence:

1. reusable `MemoryStream` destinations produced impossible 9x differences;
2. fixed buffers without observable output still allowed implausible divergence.

Those revisions led directly to the non-inlineable, checksum-observable final
harness retained in `TeeStreamSteadyStateBenchmarks.cs`.

## Environment

- BenchmarkDotNet 0.15.8
- Windows 11 build 26200.9168
- AMD Ryzen 9 5900X, 12 physical / 24 logical cores
- .NET SDK 10.0.400; .NET runtime 10.0.11 x64 RyuJIT
- Concurrent workstation GC; high-performance power plan
- 3 warmup and 8 measurement iterations per case

Command:

```text
dotnet run -c Release --no-restore --project benchmarks/TeeForge.Benchmarks -- --filter '*TeeStreamSteadyStateBenchmarks*'
```

## Retained results

| Payload | Implementation | Mean | Error | Allocated |
| ---: | --- | ---: | ---: | ---: |
| 4 KiB | Manual sequential loop | 73.66 ns | 0.96 ns | 0 B |
| 4 KiB | `TeeStream` sequential | 80.20 ns | 1.78 ns | 0 B |
| 64 KiB | Manual sequential loop | 4.441 us | 0.266 us | 0 B |
| 64 KiB | `TeeStream` sequential | 2.780 us | 0.044 us | 0 B |
| 1 MiB | Manual sequential loop | 340.220 us | 291.384 us | 0 B |
| 1 MiB | `TeeStream` sequential | 259.331 us | 91.602 us | 0 B |

## Conclusion

The 4 KiB case isolates a roughly 6.5 ns / 9% fixed wrapper cost with no managed
allocation, meeting the provisional steady-state target. The larger cases are
memory-bandwidth dominated; their apparent speedups are not interpreted as
`TeeStream` making copies faster. The 1 MiB confidence intervals are especially
wide and make that result unsuitable for a regression threshold. Future large
copy analysis should use paired hardware counters or a destination representative
of the real I/O device under audit.
