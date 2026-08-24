# TeeHashStream inline hashing cost

Status: completed on 2026-08-23.

Source state: commit `bbea61da1e8c1d3af66bffe6d805f558cdc13701`
plus the uncommitted TeeBufferedStream and TeeHashStream implementation, tests,
documentation, and benchmark harness. Raw BenchmarkDotNet artifacts are ignored
and are not part of the retained record.

## Purpose and methodology

This experiment measures the steady-state cost of writing and flushing one
payload through three configurations:

1. TeeBufferedStream with one observable, non-copying destination and no hash;
2. TeeHashStream with the same destination and SHA-256;
3. TeeHashStream with the same destination, SHA-256, and SHA-512.

Streams and hash algorithms are initialized once per benchmark case. Hashes
accumulate repeated payloads during measurement and finalize during global
cleanup. This deliberately isolates write-path hashing and dispatch from
construction, final publication, text encoding, and disposal. The baseline sink
does not copy bytes, so its near-zero time is a dispatch floor rather than a
realistic I/O comparison.

## Environment

- BenchmarkDotNet 0.15.8
- Windows 11 build 26200.9168
- AMD Ryzen 9 5900X, 12 physical / 24 logical cores
- .NET SDK 10.0.400; .NET runtime 10.0.11 x64 RyuJIT
- Concurrent workstation GC; BenchmarkDotNet high-performance power plan
- 3 warmup and 8 measurement iterations per case

Command:

```text
dotnet run -c Release --no-build --no-restore --project benchmarks/TeeForge.Benchmarks -- --filter '*TeeHashStreamBenchmarks*'
```

## Retained results

| Payload | Configuration | Mean | Error | Approx. payload rate | Allocated |
| ---: | --- | ---: | ---: | ---: | ---: |
| 4 KiB | no hash | 22.49 ns | 0.275 ns | not meaningful | 24 B |
| 4 KiB | SHA-256 | 1.871 us | 0.037 us | 2.19 GB/s | 24 B |
| 4 KiB | SHA-256 + SHA-512 | 9.025 us | 1.590 us | 0.45 GB/s | 24 B |
| 64 KiB | no hash | 25.81 ns | 5.694 ns | not meaningful | 24 B |
| 64 KiB | SHA-256 | 28.103 us | 0.521 us | 2.33 GB/s | 24 B |
| 64 KiB | SHA-256 + SHA-512 | 150.131 us | 25.227 us | 0.44 GB/s | 24 B |
| 1 MiB | no hash | 24.19 ns | 3.114 ns | not meaningful | 24 B |
| 1 MiB | SHA-256 | 448.156 us | 7.982 us | 2.34 GB/s | 24 B |
| 1 MiB | SHA-256 + SHA-512 | 2.049 ms | 0.297 ms | 0.51 GB/s | 24 B |

## Conclusion

The TeeHashStream path remains allocation-flat: zero, one, and two algorithms
all allocate 24 B per measured write-and-flush operation. SHA-256 sustains about
2.2-2.3 GB/s across the retained payload sizes. Its 1 MiB result (448.156 us)
is consistent with the earlier direct HashWriteStream lifecycle result
(457.950 us), indicating no material wrapper penalty beyond the selected hash
work on this machine.

SHA-256 plus SHA-512 runs inline and serially as designed. Its lower aggregate
payload rate records the accepted cost of the second algorithm; the benchmark
does not justify adding worker threads, owned-buffer copies, or queues. This
harness should be rerun if parallel hash execution is reconsidered.
