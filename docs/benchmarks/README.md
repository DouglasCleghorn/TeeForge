# Benchmark evidence

TeeForge keeps reproducible performance experiments in the benchmark project. Each accepted optimization must retain its BenchmarkDotNet configuration, environment details, before-and-after results, and the conclusion supported by those results.

Raw transient BenchmarkDotNet artifacts do not replace the curated experiment record. Experiment records will live beside the benchmark code so performance-motivated complexity remains auditable.

## Repository-wide sampling and retention policy

Use `eng/run-sampled-benchmark.ps1` for CSV-producing custom harnesses. It builds
Release, runs process-isolated warmups and at least five samples, records Git,
CPU, runtime, SDK, arguments and individual results, and exports separate raw
and aggregate CSVs in a unique directory. Failed runs remain recorded and make
the command fail. The harness must accept `--output`, write exactly one CSV,
and identify its runtime in a `Runtime` column. `-CaseColumns` identifies the
non-metric columns. BenchmarkDotNet exports still require conversion to the
same retained evidence schema; this runner does not parse its report format.

```powershell
./eng/run-sampled-benchmark.ps1 -BenchmarkArguments @(
    '--erasure-stream-memory', '--data-mib', '256',
    '--random-operations', '262144', '--block-size', '4096')
```

Direct erasure harness invocations are diagnostic and write unique artifact
directories. They no longer overwrite retained historical experiments.

This policy applies to every benchmark in the repository, including
BenchmarkDotNet classes, purpose-built experiment harnesses, transport tools,
driver tests, and benchmarks added outside `TeeForge.Benchmarks`.

- A retained result must use warmup followed by at least five measured samples
  for every reported case. Prefer process-isolated samples when process startup,
  tiered compilation, pooled memory, or operating-system caches can influence a
  result. A single timed pass is diagnostic only and cannot support a performance
  decision.
- Save a raw CSV with one row for every individual measured sample. Every row
  must include the UTC timestamp, Git commit hash, clean/dirty working-tree
  state, CPU model, OS, .NET runtime and SDK, build configuration, benchmark and
  case parameters, run index, operation count or elapsed duration, and every
  captured performance/resource metric. Record failures rather than silently
  dropping their rows.
- Save a separate aggregate CSV with one row per case and metric. It must include
  the sample count, mean, median, minimum, maximum, and sample standard deviation.
  Add percentiles when the sample count makes them meaningful. Use the median as
  the headline result unless the experiment record justifies another statistic.
- Name the retained files with `-raw.csv` and `-aggregate.csv` suffixes and link
  both from the dated experiment record. Do not overwrite evidence from a
  different commit, working-tree state, machine, or protocol.
- Before/after claims must sample both revisions with the same harness,
  parameters, warmup, machine, and aggregation method. If either side lacks
  equivalent samples, label the comparison directional or inconclusive rather
  than publishing a precise ratio.

BenchmarkDotNet's statistical engine satisfies the sampling requirement, but
its retained export must still follow the two-CSV schema above and include the
repository and machine provenance on every raw row. Custom harnesses must
implement equivalent warmup, sampling, and retention themselves.

## Version 0.1 targets

- BroadcastPipe stores one payload copy regardless of reader count.
- One-reader BroadcastPipe throughput and allocation remain within 15% of Microsoft `Pipe` for representative workloads.
- Multi-reader BroadcastPipe materially outperforms broadcasting through an equivalent number of independent Microsoft pipes.
- Sequential TeeStream remains within 10% of an equivalent hand-written destination loop.
- Concurrent modes demonstrate improved latency on suitably slow destinations.
- TeeBufferedStream experiments retain the small-write benefit and the
  buffer-size crossover against TeeStream.
- TeeHashStream experiments retain the incremental cost of cryptographic,
  non-cryptographic, and mixed algorithm sets against the same buffered fan-out
  without hashing. The mixed-family harness and its currently blocked local run
  are retained in
  [the algorithm-family experiment](../../benchmarks/TeeForge.Benchmarks/Experiments/2026-08-23-tee-hash-algorithm-families.md).
- HandoffStream steady-state sequential and random-access operations remain
  allocation-free. Direct-stream and equivalent manually synchronized baselines
  separate the required synchronization cost from HandoffStream delegation at
  4 KiB, 64 KiB, and 1 MiB, so normal payload throughput is not conflated with
  handoff transitions. See the retained
  [steady-state overhead experiment](../../benchmarks/TeeForge.Benchmarks/Experiments/2026-08-24-handoff-stream-overhead.md).
- Reed-Solomon experiments retain scalar-reference equivalence, zero
  steady-state allocation, and the measured gain from each managed SIMD
  backend. See the retained
  [6+2 scalar and AVX2 experiment](../../benchmarks/TeeForge.Benchmarks/Experiments/2026-08-23-reed-solomon-simd.md).
- The local-file `ErasureStream` experiment measures every power-of-two member
  block from 4 KiB through 1 MiB across sequential throughput, random IOPS, CPU,
  and memory. It supports the 128 KiB default; see the retained
  [4+2 block-size experiment](../../benchmarks/TeeForge.Benchmarks/Experiments/2026-08-26-erasure-stream-block-size.md).
  A matched
  [`RandomAccessMemoryStream` run](../../benchmarks/TeeForge.Benchmarks/Experiments/2026-08-26-erasure-stream-memory-block-size.md)
  isolates coding and cache overhead and documents the 64 KiB override for
  memory-backed or random-write-heavy sets.
- QUIC file-I/O experiments retain direct baselines, sequential transfers,
  random reads and writes at queue depths 1 through 32, and the matched
  compression comparison. See the retained
  [loopback QUIC experiment](../../benchmarks/TeeForge.Benchmarks/Experiments/2026-08-24-quic-file-io.md).
- The QUIC memory experiment repeats that matrix over a 3 GiB segmented
  `MemoryStream` backing store to isolate the transport ceiling from storage
  latency. See the retained
  [multi-gigabyte memory experiment](../../benchmarks/TeeForge.Benchmarks/Experiments/2026-08-25-quic-memory-stream.md).
- Retained experiments cover 4 KiB, 64 KiB, and 1 MiB payloads.

These are engineering targets, not package guarantees or noisy CI-blocking thresholds for version 0.1.
