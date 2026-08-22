# Retained benchmark experiments

Each experiment record must contain:

- TeeForge commit and working-tree state;
- BenchmarkDotNet version and full command;
- OS, CPU, runtime, SDK, power profile, and relevant environment details;
- complete summary tables for 4 KiB, 64 KiB, and 1 MiB payloads;
- comparison against the version 0.1 targets;
- the conclusion and any implementation decision supported by the result.

Raw `BenchmarkDotNet.Artifacts` are transient. Copy the summary and environment
section into a dated Markdown record in this directory before accepting a
performance-motivated code change.
