# Hash sink comparison: IncrementalHash versus CryptoStream

Status: completed on 2026-08-22.

Source state: commit `bbea61da1e8c1d3af66bffe6d805f558cdc13701`
plus the uncommitted hashing glossary and `HashSinkBenchmarks.cs` harness changes.
Raw BenchmarkDotNet artifacts were ignored and are not part of the retained
record.

## Purpose and methodology

This experiment compares the proposed SHA-256 `CryptoStream` hash destination
with the supplied write-only `Stream` backed directly by `IncrementalHash`.
Both implementations receive the same preallocated payload and discard their
output. Two benchmark groups separate different costs:

- steady-state benchmarks reuse initialized hash destinations and measure sync
  and async writes only;
- lifecycle benchmarks create the algorithm and stream, write once, finalize
  the hash, return its bytes, and dispose the objects.

The steady-state streams accumulate repeated payloads for the duration of a
case. This is intentional: it isolates per-write overhead without repeatedly
paying construction or finalization costs. The lifecycle cases measure the
one-payload usage pattern that exposes the hash after completion.

## Environment

- BenchmarkDotNet 0.15.8
- Windows 11 build 26200.9168
- AMD Ryzen 9 5900X, 12 physical / 24 logical cores
- .NET SDK 10.0.400; .NET runtime 10.0.11 x64 RyuJIT
- Concurrent workstation GC; BenchmarkDotNet high-performance power plan
- 3 warmup and 8 measurement iterations per case

Command:

```text
dotnet run -c Release --no-restore --project benchmarks/TeeForge.Benchmarks -- --filter '*HashSink*' --join
```

## Retained results

### Steady-state writes

| Payload | API | Implementation | Mean | Error | Allocated | CryptoStream / IncrementalHash |
| ---: | --- | --- | ---: | ---: | ---: | ---: |
| 4 KiB | Sync | `IncrementalHash` sink | 1.799 us | 0.0324 us | 0 B | 1.00x |
| 4 KiB | Sync | `CryptoStream` | 2.180 us | 0.1508 us | 0 B | 1.21x |
| 4 KiB | Async | `IncrementalHash` sink | 1.846 us | 0.0426 us | 0 B | 1.00x |
| 4 KiB | Async | `CryptoStream` | 2.010 us | 0.0468 us | 0 B | 1.09x |
| 64 KiB | Sync | `IncrementalHash` sink | 28.929 us | 0.3036 us | 0 B | 1.00x |
| 64 KiB | Sync | `CryptoStream` | 32.535 us | 0.5372 us | 0 B | 1.12x |
| 64 KiB | Async | `IncrementalHash` sink | 30.223 us | 0.4777 us | 0 B | 1.00x |
| 64 KiB | Async | `CryptoStream` | 30.772 us | 1.0187 us | 0 B | 1.02x |
| 1 MiB | Sync | `IncrementalHash` sink | 471.443 us | 8.1511 us | 0 B | 1.00x |
| 1 MiB | Sync | `CryptoStream` | 746.021 us | 15.0662 us | 0 B | 1.58x |
| 1 MiB | Async | `IncrementalHash` sink | 466.775 us | 8.0483 us | 0 B | 1.00x |
| 1 MiB | Async | `CryptoStream` | 573.026 us | 24.5730 us | 0 B | 1.23x |

### Create, write, finalize, and dispose

| Payload | Implementation | Mean | Error | Allocated | CryptoStream / IncrementalHash |
| ---: | --- | ---: | ---: | ---: | ---: |
| 4 KiB | `IncrementalHash` sink | 2.085 us | 0.0383 us | 224 B | 1.00x |
| 4 KiB | `CryptoStream` | 2.932 us | 0.1576 us | 456 B | 1.41x |
| 64 KiB | `IncrementalHash` sink | 29.358 us | 0.4683 us | 224 B | 1.00x |
| 64 KiB | `CryptoStream` | 33.664 us | 1.2327 us | 456 B | 1.15x |
| 1 MiB | `IncrementalHash` sink | 457.950 us | 4.2378 us | 224 B | 1.00x |
| 1 MiB | `CryptoStream` | 670.943 us | 5.0298 us | 456 B | 1.47x |

## Conclusion

The direct `IncrementalHash` stream is faster in every same-API comparison on
this machine. The difference is smallest for 64 KiB async writes (about 2%) and
largest for 1 MiB sync writes (about 58%). `CryptoStream` lifecycle allocation
is 456 B versus 224 B, an additional 232 B per instance.

These results support using the direct `HashWriteStream` as the hashing
destination. `TeeHashStream` will place it behind `TeeBufferedStream`, so small
logical writes can be coalesced once before ordinary and hashing destinations
receive them. The retained harness should be rerun when TeeHashStream is
implemented so the complete fan-out path can be measured separately from the
hash sink itself.

The version 0.1 benchmark targets do not define a hash-sink budget, so this is a
baseline experiment rather than a pass/fail gate.
