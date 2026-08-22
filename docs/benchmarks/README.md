# Benchmark evidence

TeeForge keeps reproducible performance experiments in the benchmark project. Each accepted optimization must retain its BenchmarkDotNet configuration, environment details, before-and-after results, and the conclusion supported by those results.

Raw transient BenchmarkDotNet artifacts do not replace the curated experiment record. Experiment records will live beside the benchmark code so performance-motivated complexity remains auditable.

## Version 0.1 targets

- TeePipe stores one payload copy regardless of reader count.
- One-reader TeePipe throughput and allocation remain within 15% of Microsoft `Pipe` for representative workloads.
- Multi-reader TeePipe materially outperforms broadcasting through an equivalent number of independent Microsoft pipes.
- Sequential TeeStream remains within 10% of an equivalent hand-written destination loop.
- Concurrent modes demonstrate improved latency on suitably slow destinations.
- Retained experiments cover 4 KiB, 64 KiB, and 1 MiB payloads.

These are engineering targets, not package guarantees or noisy CI-blocking thresholds for version 0.1.
