# QUIC multi-gigabyte memory-stream benchmark

Date: 2026-08-25

Commit: `6397884`, with a dirty working tree containing the QUIC implementation,
benchmark applications, and unrelated in-progress user changes.

## Question

When storage latency is removed, what throughput does the mutually authenticated
QUIC path sustain for a multi-gigabyte stream, and how do random block size and
queue depth affect it?

## Representation

`System.IO.MemoryStream` exposes `int` capacity and cannot hold a true 2+ GiB
logical stream. This benchmark uses `SegmentedMemoryStore`, a benchmark-only
3 GiB logical stream composed of 48 independently allocated 64 MiB
`MemoryStream` segments. It exposes normal sequential `Stream` cursors and
thread-safe positioned operations with one lock per segment.

The server and client each allocate and initialize their own 3 GiB store with
deterministic pseudo-random bytes before timing begins. Thus the run uses 6 GiB
of payload memory, excluding runtime and transport buffers. Sequential reads
consume the complete 3 GiB store. Sequential writes overwrite it using a reused
deterministic block. Random cases transfer 64 MiB per iteration.

The QUIC architecture is identical to the file benchmark: one dynamic named
stream per sequential transfer and one native QUIC stream per positioned
operation. Direct cases use the same segmented store without networking.

## Environment and command

- Windows 11 Pro 10.0.26200, build 26200
- AMD Ryzen 9 5900X, 12 cores / 24 logical processors
- 63.9 GiB physical RAM; 36.6 GiB free immediately before the run
- .NET SDK 10.0.400; runtime .NET 10.0.11
- Release build, separate client/server processes, IPv4 loopback, mTLS
- 3 GiB logical store per peer, 64 MiB segments
- One sequential iteration; two random iterations
- 4 KiB, 64 KiB, and 1 MiB random blocks; QD1, QD4, QD16, and QD32
- Compression disabled

Exact commands are in the
[benchmark README](../../TeeForge.QuicBench.Shared/README.md). The initial raw
run is `artifacts/quic-memory-benchmark/3gib-results-none.json`. The matched
backing-store comparison is
`artifacts/quic-memory-benchmark/3gib-direct-comparison.json`. `artifacts` is
ignored, so this document is the retained result.

## Sequential results

Each case transferred the complete 3 GiB logical stream.

| Operation | Block | Direct MiB/s | QUIC MiB/s | QUIC/direct |
|---|---:|---:|---:|---:|
| Read | 64 KiB | 24,806.0 | 252.8 | 1.02% |
| Read | 1 MiB | 19,133.7 | 240.0 | 1.25% |
| Write | 64 KiB | 14,245.8 | 250.9 | 1.76% |
| Write | 1 MiB | 19,119.5 | 241.2 | 1.26% |

The direct path measures memory copies plus the segmented-stream lock and cursor
logic, not raw DRAM bandwidth. QUIC remains near 240–253 MiB/s, closely matching
the file run. That demonstrates the local disk was not the limiting component
of the sequential QUIC result.

## Backing-store overhead comparison

The comparison run adds `QUIC-Direct`, which uses the same connection, mTLS,
named-stream preface, native QUIC streams, client buffers, and operation sizes.
For reads the server repeats a generated block instead of reading the segmented
store. For writes it receives and discards the payload instead of copying it
into the store. Synthetic random-access endpoints use the same request protocol
but generate reads and discard writes.

Positive overhead means `QUIC-Memory` was slower than `QUIC-Direct`. Negative
values mean the memory-backed path happened to be faster and should be treated
as run-to-run noise, not a backing-store benefit.

### Sequential comparison

| Operation | Block | QUIC-Memory MiB/s | QUIC-Direct MiB/s | Store overhead |
|---|---:|---:|---:|---:|
| Read | 64 KiB | 226.3 | 230.3 | 1.7% |
| Read | 1 MiB | 238.0 | 242.5 | 1.9% |
| Write | 64 KiB | 238.4 | 246.8 | 3.4% |
| Write | 1 MiB | 246.2 | 237.4 | -3.7% |

The median sequential overhead is 1.8%. The reversed 1 MiB write result bounds
the noise in this single-iteration, multi-gigabyte comparison.

### Random read comparison

| Block | QD | QUIC-Memory MiB/s | QUIC-Direct MiB/s | Store overhead |
|---:|---:|---:|---:|---:|
| 4 KiB | 1 | 19.1 | 16.7 | -14.3% |
| 4 KiB | 4 | 35.1 | 50.7 | 30.8% |
| 4 KiB | 16 | 92.9 | 96.3 | 3.5% |
| 4 KiB | 32 | 111.8 | 123.0 | 9.1% |
| 64 KiB | 1 | 123.4 | 125.2 | 1.5% |
| 64 KiB | 4 | 203.2 | 201.3 | -1.0% |
| 64 KiB | 16 | 223.7 | 222.9 | -0.4% |
| 64 KiB | 32 | 214.5 | 198.7 | -8.0% |
| 1 MiB | 1 | 205.2 | 206.3 | 0.5% |
| 1 MiB | 4 | 225.3 | 224.9 | -0.2% |
| 1 MiB | 16 | 209.7 | 201.6 | -4.0% |
| 1 MiB | 32 | 218.9 | 192.6 | -13.7% |

Across all random-read cases and iterations, total bytes divided by total time
was 84.8 MiB/s for `QUIC-Memory` and 85.5 MiB/s for `QUIC-Direct`: 0.9%
aggregate store overhead.

### Random write comparison

| Block | QD | QUIC-Memory MiB/s | QUIC-Direct MiB/s | Store overhead |
|---:|---:|---:|---:|---:|
| 4 KiB | 1 | 21.7 | 21.2 | -2.6% |
| 4 KiB | 4 | 53.8 | 52.3 | -2.7% |
| 4 KiB | 16 | 104.3 | 84.9 | -22.8% |
| 4 KiB | 32 | 124.9 | 119.9 | -4.2% |
| 64 KiB | 1 | 137.4 | 131.5 | -4.4% |
| 64 KiB | 4 | 201.3 | 204.6 | 1.6% |
| 64 KiB | 16 | 212.6 | 231.6 | 8.2% |
| 64 KiB | 32 | 216.3 | 200.6 | -7.8% |
| 1 MiB | 1 | 199.1 | 196.1 | -1.6% |
| 1 MiB | 4 | 208.4 | 204.7 | -1.8% |
| 1 MiB | 16 | 164.7 | 168.7 | 2.4% |
| 1 MiB | 32 | 145.4 | 145.1 | -0.2% |

Aggregate random-write throughput was 94.7 MiB/s for `QUIC-Memory` and
90.9 MiB/s for `QUIC-Direct`. The apparent -4.2% overhead, together with the
mixed signs above, shows that scheduler and transport variance is larger than
the backing-store cost in this run.

## Random read results

Medians of two 64 MiB iterations:

| Block | QD | Direct MiB/s | QUIC MiB/s | QUIC IOPS |
|---:|---:|---:|---:|---:|
| 4 KiB | 1 | 6,970.3 | 21.0 | 5,379 |
| 4 KiB | 4 | 9,655.4 | 54.0 | 13,836 |
| 4 KiB | 16 | 9,147.1 | 100.9 | 25,824 |
| 4 KiB | 32 | 9,822.1 | 132.2 | 33,832 |
| 64 KiB | 1 | 19,779.0 | 136.6 | 2,186 |
| 64 KiB | 4 | 20,808.3 | 202.4 | 3,238 |
| 64 KiB | 16 | 20,439.2 | 225.9 | 3,615 |
| 64 KiB | 32 | 17,073.5 | 221.2 | 3,540 |
| 1 MiB | 1 | 14,855.4 | 199.4 | 199 |
| 1 MiB | 4 | 13,087.2 | 224.4 | 224 |
| 1 MiB | 16 | 9,016.3 | 216.3 | 216 |
| 1 MiB | 32 | 5,873.6 | 210.0 | 210 |

## Random write results

| Block | QD | Direct MiB/s | QUIC MiB/s | QUIC IOPS |
|---:|---:|---:|---:|---:|
| 4 KiB | 1 | 6,212.7 | 25.0 | 6,398 |
| 4 KiB | 4 | 8,131.0 | 57.8 | 14,806 |
| 4 KiB | 16 | 7,958.4 | 112.7 | 28,846 |
| 4 KiB | 32 | 7,847.6 | 132.3 | 33,877 |
| 64 KiB | 1 | 13,368.6 | 146.3 | 2,341 |
| 64 KiB | 4 | 10,781.1 | 204.7 | 3,276 |
| 64 KiB | 16 | 6,701.9 | 226.5 | 3,623 |
| 64 KiB | 32 | 4,563.2 | 221.3 | 3,541 |
| 1 MiB | 1 | 8,054.4 | 194.6 | 195 |
| 1 MiB | 4 | 2,833.7 | 212.2 | 212 |
| 1 MiB | 16 | 794.3 | 175.9 | 176 |
| 1 MiB | 32 | 403.1 | 146.5 | 147 |

## Interpretation

- Small positioned operations need concurrency. At 4 KiB, reads improved from
  5,379 IOPS at QD1 to 33,832 IOPS at QD32, while writes improved from 6,398 to
  33,877 IOPS.
- QD4 is sufficient for 1 MiB operations. Additional concurrency reduces both
  direct and QUIC throughput, especially for writes.
- The 64 KiB QUIC path reaches its plateau around QD16 at roughly 226 MiB/s.
- Memory makes the relative QUIC overhead larger than in the file benchmark,
  but absolute QUIC throughput is nearly unchanged. Encryption, framing,
  loopback networking, scheduling, per-operation stream setup, and the second
  process dominate once storage latency disappears.
- High-QD direct large writes are intentionally reported rather than hidden.
  They expose task scheduling and per-segment lock contention in this reference
  implementation; they do not represent maximum possible DRAM bandwidth.
- Comparing memory-backed QUIC with transfer-only QUIC finds no material random
  access penalty and about 2% sequential overhead. The segmented store did not
  create the roughly 250 MiB/s ceiling.

## Conclusion

The multi-gig test confirms the connection can transfer beyond the native
`MemoryStream` capacity boundary without length truncation. The current QUIC
implementation is transport-bound near 250 MiB/s on this machine. For random
access, a moderate stream pool or caller queue depth is valuable, but QD beyond
4 for 1 MiB operations and beyond roughly 16 for 64 KiB operations is wasteful.
The segmented `MemoryStream` layer adds a small sequential copy cost and no
measurable aggregate random-access cost relative to transfer-only QUIC.
