# SHA-256 stream API comparison

Status: superseded on 2026-08-22 by
`2026-08-22-sha256-stream-apis-hash-write-stream.md`, which uses the supplied
HashWriteStream rather than a manual IncrementalHash read loop.

Source state: commit `bbea61da1e8c1d3af66bffe6d805f558cdc13701`
plus the uncommitted hashing design, benchmark harnesses, and retained experiment
records. Raw BenchmarkDotNet artifacts were ignored and are not part of this
record.

## Purpose and methodology

This experiment compares three ways to compute a SHA-256 hash from a completed
`MemoryStream`:

1. create an `IncrementalHash`, read the stream through a reusable 81,920-byte
   buffer, append each block, and finalize it;
2. call `SHA256.HashData(Stream)`;
3. call `SHA256.Create().ComputeHash(Stream)` and dispose the algorithm.

Each method receives its own read-only `MemoryStream` over the same random byte
array and resets its stream position before each invocation. Construction and
finalization of the hash implementation and allocation of the returned 32-byte
hash are included. The IncrementalHash read buffer is created once during global
setup and is not included in per-operation allocation. Global setup verifies
that all three methods return the same bytes before any measurements run.

An initial launch produced no measurements because BenchmarkDotNet rejects
sealed benchmark classes. The class was made unsealed before the retained run.

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

| Payload | Implementation | Mean | Error | Allocated | Ratio to IncrementalHash |
| ---: | --- | ---: | ---: | ---: | ---: |
| 4 KiB | `IncrementalHash` read loop | 2.416 us | 0.0854 us | 192 B | 1.00x |
| 4 KiB | `SHA256.HashData(Stream)` | 2.241 us | 0.0874 us | 88 B | 0.93x |
| 4 KiB | `SHA256.Create().ComputeHash(Stream)` | 2.326 us | 0.1024 us | 248 B | 0.96x |
| 64 KiB | `IncrementalHash` read loop | 29.913 us | 0.4080 us | 192 B | 1.00x |
| 64 KiB | `SHA256.HashData(Stream)` | 29.763 us | 0.4234 us | 88 B | 1.00x |
| 64 KiB | `SHA256.Create().ComputeHash(Stream)` | 29.632 us | 0.7188 us | 248 B | 0.99x |
| 1 MiB | `IncrementalHash` read loop | 468.608 us | 6.2153 us | 192 B | 1.00x |
| 1 MiB | `SHA256.HashData(Stream)` | 470.070 us | 7.1421 us | 88 B | 1.00x |
| 1 MiB | `SHA256.Create().ComputeHash(Stream)` | 466.920 us | 6.9950 us | 248 B | 1.00x |

## Conclusion

`SHA256.HashData(Stream)` is the strongest one-shot stream API in this test. It
is about 7% faster than the IncrementalHash read loop for 4 KiB, statistically
indistinguishable at 64 KiB and 1 MiB, and allocates 88 B rather than 192 B.
`SHA256.Create().ComputeHash(Stream)` has similar throughput but the highest
allocation at 248 B.

This does not replace a streaming hash destination when the source cannot be
replayed after consumption. It is directly relevant to a TeeHashPipe hidden
reader, which already owns a stream-like sequence that it can consume to EOF on
its worker task. Selecting it for that implementation remains a design decision
rather than an automatic consequence of this microbenchmark.

The version 0.1 benchmark targets do not define a hashing budget, so this is a
baseline experiment rather than a pass/fail gate.
