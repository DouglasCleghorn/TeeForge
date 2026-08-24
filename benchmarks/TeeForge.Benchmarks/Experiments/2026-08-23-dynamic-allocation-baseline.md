# Dynamic allocation stream baseline

Date: 2026-08-23

## Goal

Track the uncontended serialization and BAT-lookup overhead for overwriting an
already allocated block, and separately track the cost of a first sparse write.
This is the baseline requested before tuning the operation gate, metadata cache,
free-block queue capacity, or low watermark.

## Benchmarks

`DynamicAllocationStreamBenchmarks` compares 4 KiB and 64 KiB overwrites of an
already allocated block with a preallocated `MemoryStream`. The sparse case
creates a 64 KiB-block image and first-writes logical block 1024. Free-space
background discovery is disabled so its scheduling does not contaminate the
allocation measurement.

Run on an otherwise idle machine with:

```text
dotnet run --project benchmarks/TeeForge.Benchmarks -c Release --filter *DynamicAllocationStream*
```

This commit establishes the reproducible workload. Curated timing and allocation
results should be added after a stable release build is measured on the target
hardware; no performance threshold is inferred from a short CI or dry run.
