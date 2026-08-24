# Reed-Solomon managed SIMD coding loop

Status: completed on 2026-08-23.

Source state: commit `bbea61da1e8c1d3af66bffe6d805f558cdc13701`
plus the uncommitted ErasureCodeStream format baseline, scalar codec, AVX2/SSSE3
coding loops, tests, and benchmark harness. Raw BenchmarkDotNet artifacts are
ignored and are not part of the retained record.

## Purpose and methodology

This experiment measures 6+2 systematic Reed-Solomon parity generation over
GF(2^8) with the version-1 Vandermonde matrix. The scalar reference uses table
multiplication one byte at a time. The hardware-accelerated path uses AVX2 nibble
tables and byte shuffles on this machine; SSSE3 is the managed fallback on older
x64 hardware.

Six data buffers and two reusable parity buffers are initialized once. Each
benchmark operation regenerates both parity shards over the selected range. It
does not include disk I/O, checksums, buffer rental, matrix construction, or
recovery. Scalar/SIMD byte equivalence and reconstruction are enforced separately
by unit tests.

## Environment

- BenchmarkDotNet 0.15.8
- Windows 11 build 26200.9168
- AMD Ryzen 9 5900X, 12 physical / 24 logical cores
- .NET SDK 10.0.400; .NET runtime 10.0.11 x64 RyuJIT
- AVX2, SSSE3, and 256-bit vectors available
- Concurrent workstation GC; BenchmarkDotNet high-performance power plan
- 3 warmup and 8 measurement iterations per case

Command:

```text
dotnet run --project benchmarks/TeeForge.Benchmarks/TeeForge.Benchmarks.csproj -c Release --no-restore -- --filter *ReedSolomonBenchmarks*
```

## Retained results

| Shard size | Implementation | Mean | Error | Ratio | Logical data rate | Allocated |
| ---: | --- | ---: | ---: | ---: | ---: | ---: |
| 64 KiB | scalar | 924.85 us | 7.536 us | 1.00 | 0.43 GB/s | 0 B |
| 64 KiB | AVX2 | 23.59 us | 0.266 us | 0.03 | 16.67 GB/s | 0 B |
| 1 MiB | scalar | 15.195 ms | 0.148 ms | 1.00 | 0.41 GB/s | 0 B |
| 1 MiB | AVX2 | 397.04 us | 4.169 us | 0.03 | 15.85 GB/s | 0 B |

The logical data rate divides `6 * ShardSize` by mean time. The coding loop also
writes two parity shards, so it is not a disk-throughput prediction.

## Conclusion

The AVX2 path is approximately 39.2 times faster at 64 KiB and 38.3 times faster
at 1 MiB while retaining zero steady-state allocation. This is a material gain and
justifies the managed nibble-shuffle implementation. Its roughly 16 GB/s logical
data rate leaves substantial headroom over a typical erasure set's member I/O.

The experiment does not justify a native Rust or C dependency for version 1. The
managed scalar backend remains the correctness oracle and portable fallback. ARM64
AdvSimd is not yet implemented or measured and remains a required follow-up before
claiming cross-architecture SIMD coverage.
