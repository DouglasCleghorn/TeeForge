# HandoffStream steady-state overhead

Status: completed on 2026-08-24.

Source state: commit `aec719342044c72211e4281abc537bae1a11ab6e`
plus an uncommitted working tree containing HandoffStream, its tests and
documentation, this benchmark harness, and concurrent unrelated erasure-coding
and networking work. Raw BenchmarkDotNet artifacts are ignored and are not part
of the retained record.

## Purpose and methodology

This experiment measures the steady-state cost of routing normal operations
through `HandoffStream`. A handoff is intentionally not performed inside the
measured path. The matrix covers synchronous writes, asynchronously invoked
writes, native `ITeeRandomAccessStream` reads, and native random-access writes at
4 KiB, 64 KiB, and 1 MiB.

The destination is a non-copying observing stream. It consumes the supplied
span length and endpoint bytes but performs no payload copy or I/O. This makes
the benchmark a deliberately hostile upper bound for relative wrapper overhead:
the direct baseline takes only a few nanoseconds, while storage and network
latency cannot hide synchronization cost. It also verifies that the cost is
fixed rather than proportional to payload size.

Each invocation performs two operations over the same buffers. Direct rows
perform two direct calls; HandoffStream rows perform one direct and one handoff
call; manually synchronized rows perform one direct and one call protected by a
`SemaphoreSlim`. `OperationsPerInvoke = 2`, so BenchmarkDotNet reports the mean
per operation. This paired design keeps the working set identical in every
isolated benchmark process.

## Environment

- BenchmarkDotNet 0.15.8
- Windows 11 build 26200.9168 (25H2)
- AMD Ryzen 9 5900X, 12 physical / 24 logical cores
- .NET SDK 10.0.400; .NET runtime 10.0.11 x64 RyuJIT
- Concurrent workstation GC; BenchmarkDotNet high-performance power plan
- 3 warmup and 8 measurement iterations per case

Command:

```text
dotnet run -c Release --no-restore --project benchmarks/TeeForge.Benchmarks -- --filter "*HandoffStreamBenchmarks*" --artifacts "BenchmarkDotNet.Artifacts/handoff-stream-overhead"
```

## Retained results

All rows reported 0 B of managed allocation. Times below are the paired
per-operation means described above.

### Sequential writes

| Payload | Direct | HandoffStream | Manual synchronization |
| ---: | ---: | ---: | ---: |
| 4 KiB | 1.566 ns | 9.798 ns | 9.823 ns |
| 64 KiB | 1.581 ns | 10.090 ns | 10.132 ns |
| 1 MiB | 1.588 ns | 9.948 ns | 10.182 ns |

### Asynchronously invoked sequential writes

| Payload | Direct | HandoffStream | Manual synchronization |
| ---: | ---: | ---: | ---: |
| 4 KiB | 5.014 ns | 21.094 ns | 12.140 ns |
| 64 KiB | 4.989 ns | 23.115 ns | 11.874 ns |
| 1 MiB | 4.942 ns | 22.651 ns | 11.997 ns |

### Native random-access reads

| Payload | Direct | HandoffStream | Manual synchronization |
| ---: | ---: | ---: | ---: |
| 4 KiB | 2.340 ns | 12.636 ns | 11.264 ns |
| 64 KiB | 2.498 ns | 13.030 ns | 11.337 ns |
| 1 MiB | 2.350 ns | 12.628 ns | 11.297 ns |

### Native random-access writes

| Payload | Direct | HandoffStream | Manual synchronization |
| ---: | ---: | ---: | ---: |
| 4 KiB | 1.675 ns | 11.242 ns | 10.019 ns |
| 64 KiB | 1.711 ns | 10.999 ns | 9.933 ns |
| 1 MiB | 1.690 ns | 11.160 ns | 9.961 ns |

## Rejected harness revisions

Two earlier revisions copied payloads into fixed byte arrays. Their 1 MiB
results were rejected: separate BenchmarkDotNet processes received sharply
different effective memory bandwidth and produced contradictory ratios, at
times making equivalent wrappers appear 12 times slower or making the handoff
path appear 56% faster than direct access. Pairing copies did not remove that
process-level variance. Those results are not performance evidence and are not
retained.

The observing-stream design removes the unstable memory-copy component and
measures only behavior owned by the wrapper.

## Conclusion

The version 0.1 target is met for normal steady-state use. Every handoff path is
allocation-free, and the measured cost is independent of whether the caller
supplies 4 KiB, 64 KiB, or 1 MiB.

Synchronous sequential writes match the equivalent manual synchronization
control within noise. Native random-access routing adds roughly 1-2 ns per
reported paired operation beyond that control. The async wrapper adds roughly
9-11 ns per reported paired operation beyond the async manual control because of
its additional async delegation boundary. These fixed nanosecond costs are not a
claim of literal zero overhead, but they do not create a payload-dependent
throughput penalty and will be immaterial for normal stream I/O.
