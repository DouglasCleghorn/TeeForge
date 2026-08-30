# Retained benchmark experiments

All benchmark projects and tools in this repository follow the
[repository-wide sampling and retention policy](../../../docs/benchmarks/README.md#repository-wide-sampling-and-retention-policy).

Each experiment record must contain:

- TeeForge commit and working-tree state;
- BenchmarkDotNet version and full command;
- OS, CPU, runtime, SDK, power profile, and relevant environment details;
- complete summary tables for 4 KiB, 64 KiB, and 1 MiB payloads;
- comparison against the version 0.1 targets;
- the conclusion and any implementation decision supported by the result.
- links to the per-sample `-raw.csv` and statistical `-aggregate.csv` files
  required by the repository-wide policy.

Raw `BenchmarkDotNet.Artifacts` are transient. Copy the summary and environment
section into a dated Markdown record in this directory before accepting a
performance-motivated code change.

Purpose-built end-to-end harnesses may be used where BenchmarkDotNet cannot
represent multi-file resource sampling cleanly. Such records must retain their
command, sampled raw and aggregate CSVs, charts, sampling definitions, and
limitations. The
[`ErasureStream` 4+2 block-size experiment](2026-08-26-erasure-stream-block-size.md)
is the first example.

The matched
[`RandomAccessMemoryStream` experiment](2026-08-26-erasure-stream-memory-block-size.md)
uses the same matrix without a storage device so cache and coding overhead can
be separated from file-I/O behavior.
