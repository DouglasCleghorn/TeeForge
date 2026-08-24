# TeeHashStream cryptographic and non-cryptographic algorithm cost

Status: benchmark harness completed on 2026-08-23; measurement blocked by the
local execution sandbox.

## Purpose and methodology

This follow-up extends `TeeHashStreamBenchmarks` to measure five configurations
over 4 KiB, 64 KiB, and 1 MiB payloads:

1. TeeBufferedStream with no hash;
2. SHA-256 through the original `HashAlgorithmName` path;
3. SHA-256 plus SHA-512 through the original path;
4. XXH3 through `TeeHashAlgorithm`;
5. mixed SHA-256 plus XXH3 through `TeeHashAlgorithm`.

Streams and algorithms are initialized once per benchmark case. Each operation
writes and flushes one preallocated payload, isolating steady-state hashing and
fan-out from construction and final result publication. The same
`TeeBufferedStreamOptions` and observable non-copying destination are used by
every case.

Command:

```text
dotnet run --project benchmarks/TeeForge.Benchmarks/TeeForge.Benchmarks.csproj \
  -c Release --no-build --no-restore -- \
  --filter '*TeeHashStreamBenchmarks*' --join
```

## Attempted run

BenchmarkDotNet discovered all 15 cases, but its generated project performs an
internal restore. The managed execution sandbox denied access to the user-level
`NuGet.Config`, so BenchmarkDotNet stopped before running any measurement. No
timing or allocation result is claimed from that attempt.

The retained harness builds successfully in Release. Rerun the command in an
environment that permits BenchmarkDotNet to read the normal NuGet and hardware
configuration, then replace this blocked status with the complete result table
and conclusion.
