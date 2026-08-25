# Benchmark evidence

TeeForge keeps reproducible performance experiments in the benchmark project. Each accepted optimization must retain its BenchmarkDotNet configuration, environment details, before-and-after results, and the conclusion supported by those results.

Raw transient BenchmarkDotNet artifacts do not replace the curated experiment record. Experiment records will live beside the benchmark code so performance-motivated complexity remains auditable.

## Version 0.1 targets

- TeePipe stores one payload copy regardless of reader count.
- One-reader TeePipe throughput and allocation remain within 15% of Microsoft `Pipe` for representative workloads.
- Multi-reader TeePipe materially outperforms broadcasting through an equivalent number of independent Microsoft pipes.
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
- Retained experiments cover 4 KiB, 64 KiB, and 1 MiB payloads.

These are engineering targets, not package guarantees or noisy CI-blocking thresholds for version 0.1.
