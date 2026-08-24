# SHA-256 stream API comparison using HashWriteStream

Status: completed on 2026-08-22. This is the retained replacement for the
manual IncrementalHash read-loop experiment.

Source state: commit `bbea61da1e8c1d3af66bffe6d805f558cdc13701`
plus the uncommitted hashing design, benchmark harnesses, and retained experiment
records. Raw BenchmarkDotNet artifacts were ignored and are not part of this
record.

## Purpose and methodology

This experiment compares three ways to compute a SHA-256 hash from a completed
`MemoryStream`:

1. create the supplied IncrementalHash-backed `HashWriteStream`, copy the source
   into it with `MemoryStream.CopyTo` and an 81,920-byte requested buffer size,
   then call `GetHashAndReset` and dispose it;
2. call `SHA256.HashData(Stream)`;
3. call `SHA256.Create().ComputeHash(Stream)` and dispose the algorithm.

Each method receives its own read-only `MemoryStream` over the same random byte
array and resets its stream position before each invocation. Hash construction,
stream construction where applicable, finalization, disposal, and allocation of
the returned 32-byte hash are included. Global setup verifies that all three
methods return the same bytes before any measurements run.

This replaces the earlier retained run that used a manual IncrementalHash read
loop. The replacement was requested so the benchmark represents the actual
HashWriteStream abstraction under consideration.

## Environment

- BenchmarkDotNet 0.15.8
- Windows 11 build 26200.9168
- AMD Ryzen 9 5900X, 12 physical / 24 logical cores
- .NET SDK 10.0.400; .NET runtime 10.0.11 x64 RyuJIT
- Concurrent workstation GC; BenchmarkDotNet high-performance power plan
- 3 warmup and 8 measurement iterations per case

Command:

```text
dotnet run -c Release --no-restore --project benchmarks/TeeForge.Benchmarks -- --filter '*HashStreamApiBenchmarks*' --join
```

## Retained results

| Payload | Implementation | Mean | Error | Allocated | Ratio to HashWriteStream |
| ---: | --- | ---: | ---: | ---: | ---: |
| 4 KiB | `MemoryStream.CopyTo(HashWriteStream)` | 2.399 us | 0.0440 us | 224 B | 1.00x |
| 4 KiB | `SHA256.HashData(Stream)` | 2.376 us | 0.0870 us | 88 B | 0.99x |
| 4 KiB | `SHA256.Create().ComputeHash(Stream)` | 2.498 us | 0.1242 us | 248 B | 1.04x |
| 64 KiB | `MemoryStream.CopyTo(HashWriteStream)` | 29.483 us | 0.3894 us | 224 B | 1.00x |
| 64 KiB | `SHA256.HashData(Stream)` | 30.560 us | 0.5519 us | 88 B | 1.04x |
| 64 KiB | `SHA256.Create().ComputeHash(Stream)` | 30.620 us | 0.6981 us | 248 B | 1.04x |
| 1 MiB | `MemoryStream.CopyTo(HashWriteStream)` | 453.094 us | 7.2946 us | 224 B | 1.00x |
| 1 MiB | `SHA256.HashData(Stream)` | 474.416 us | 16.7290 us | 88 B | 1.05x |
| 1 MiB | `SHA256.Create().ComputeHash(Stream)` | 465.777 us | 12.5442 us | 248 B | 1.03x |

## Conclusion

HashWriteStream has the best throughput profile in this run. It is effectively
tied with `SHA256.HashData(Stream)` at 4 KiB, about 4% faster at 64 KiB, and about
5% faster at 1 MiB. `SHA256.HashData(Stream)` retains a clear allocation
advantage at 88 B versus 224 B. `SHA256.Create().ComputeHash(Stream)` is neither
the throughput nor allocation leader in this matrix.

This benchmark favors HashWriteStream when throughput is the primary selection
criterion and favors `HashData(Stream)` when minimizing per-operation allocation
is primary. It does not by itself measure TeePipe scheduling, backpressure, or an
asynchronous source and therefore does not select the TeeHashPipe implementation
without an end-to-end pipe benchmark.

The version 0.1 benchmark targets do not define a hashing budget, so this is a
baseline experiment rather than a pass/fail gate.
