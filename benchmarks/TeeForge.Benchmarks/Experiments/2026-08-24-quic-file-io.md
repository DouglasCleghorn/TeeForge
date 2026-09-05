# QUIC file I/O: direct, sequential, random, and queue depth

Date: 2026-08-24

Commit: `6397884`, with a dirty working tree containing the QUIC implementation,
benchmark applications, and unrelated in-progress user changes.

## Question

What overhead does TeeForge's mutually authenticated QUIC connection add to
local file reads and writes, how much concurrency does random access need, and
does transparent Brotli compression help at this layer?

## Harness

The retained harness consists of separate
`TeeForge.QuicBench.Server` and `TeeForge.QuicBench.Client` executables. They use
local PEM certificate and key files, pin the peer certificate, and connect over
IPv4 loopback. After the connection is authenticated, the server registers
`random-read` and `random-write` endpoints and sends a readiness stream. This
explicit handshake prevents the client from racing endpoint registration.

Sequential transfers open a dynamic named stream for each iteration. Random
transfers use a long-lived random-access channel and one native bidirectional
QUIC stream per operation, so queue depth maps to concurrent QUIC streams rather
than an application-level request multiplexer. Direct baselines use `FileStream`
for sequential work and `System.IO.RandomAccess` for positioned work.

The source file is deterministic pseudo-random data. It is intentionally not
compressible. Both paths get a warm-up operation before measurement. The
sequential cases transfer 64 MiB three times; each random case transfers 8 MiB
twice. The tables report the median MiB/s.

## Environment

- Windows 11 Pro 10.0.26200, build 26200
- AMD Ryzen 9 5900X, 12 cores / 24 logical processors
- Samsung SSD 990 PRO 2 TB, NVMe, backing the `C:` workspace
- .NET SDK 10.0.400; runtime recorded by the client as .NET 10.0.11
- Release build, server and client as separate processes, IPv4 loopback
- 64 MiB source; 4 KiB, 64 KiB, and 1 MiB random blocks
- Queue depths 1, 4, 16, and 32
- Uncompressed run, then a matched `BrotliFastest` run with a 16 KiB random
  compression threshold

This is a warm-cache, end-to-end software-path test, not a durable-storage or
network-capacity test. `FlushAsync` is included for sequential writes, but no
write-through or device cache flush is requested. Random write flushing is
outside the timed region. OS page cache effects explain direct read rates above
the physical device's sustained throughput. CPU power profile and background
OneDrive activity were not controlled.

## Results

### Sequential throughput

| Operation | Block | Direct MiB/s | QUIC none MiB/s | QUIC fastest MiB/s | QUIC/direct |
|---|---:|---:|---:|---:|---:|
| Read | 64 KiB | 2,539.4 | 238.4 | 166.0 | 9.4% |
| Read | 1 MiB | 5,543.6 | 243.1 | 212.0 | 4.4% |
| Write | 64 KiB | 920.6 | 285.1 | 160.4 | 31.0% |
| Write | 1 MiB | 1,562.4 | 239.7 | 206.1 | 15.3% |

`QUIC/direct` compares the uncompressed QUIC result with the direct result from
the same run. Loopback QUIC plateaued near 240â€“285 MiB/s; larger blocks raised
the cached direct baseline but did not raise QUIC throughput.

### Random read throughput

| Block | QD | Direct MiB/s | QUIC none MiB/s | QUIC fastest MiB/s | QUIC/direct |
|---:|---:|---:|---:|---:|---:|
| 4 KiB | 1 | 203.5 | 17.1 | 15.4 | 8.4% |
| 4 KiB | 4 | 302.3 | 44.0 | 44.8 | 14.6% |
| 4 KiB | 16 | 336.4 | 92.5 | 93.5 | 27.5% |
| 4 KiB | 32 | 360.2 | 85.9 | 114.7 | 23.9% |
| 64 KiB | 1 | 1,736.6 | 106.7 | 89.2 | 6.1% |
| 64 KiB | 4 | 2,335.7 | 181.2 | 132.2 | 7.8% |
| 64 KiB | 16 | 2,640.7 | 180.9 | 144.0 | 6.9% |
| 64 KiB | 32 | 2,472.2 | 184.5 | 147.1 | 7.5% |
| 1 MiB | 1 | 5,842.5 | 190.9 | 149.6 | 3.3% |
| 1 MiB | 4 | 4,959.6 | 234.7 | 196.5 | 4.7% |
| 1 MiB | 16 | 3,998.4 | 234.0 | 211.4 | 5.9% |
| 1 MiB | 32 | 3,568.0 | 236.0 | 216.0 | 6.6% |

### Random write throughput

| Block | QD | Direct MiB/s | QUIC none MiB/s | QUIC fastest MiB/s | QUIC/direct |
|---:|---:|---:|---:|---:|---:|
| 4 KiB | 1 | 163.9 | 19.0 | 18.8 | 11.6% |
| 4 KiB | 4 | 294.4 | 51.6 | 51.2 | 17.5% |
| 4 KiB | 16 | 344.8 | 97.9 | 85.1 | 28.4% |
| 4 KiB | 32 | 345.6 | 111.6 | 99.3 | 32.3% |
| 64 KiB | 1 | 1,540.4 | 112.1 | 96.4 | 7.3% |
| 64 KiB | 4 | 1,651.3 | 185.0 | 155.6 | 11.2% |
| 64 KiB | 16 | 931.8 | 140.9 | 156.7 | 15.1% |
| 64 KiB | 32 | 574.7 | 160.2 | 143.8 | 27.9% |
| 1 MiB | 1 | 1,150.6 | 176.9 | 130.2 | 15.4% |
| 1 MiB | 4 | 371.4 | 146.1 | 135.3 | 39.3% |
| 1 MiB | 16 | 188.7 | 108.3 | 105.4 | 57.4% |
| 1 MiB | 32 | 200.4 | 116.8 | 104.7 | 58.3% |

The high QUIC/direct percentages for 1 MiB writes at QD16 and QD32 are caused
by the direct write baseline falling under excessive concurrency; QUIC itself
also slows. They are not a QUIC speedup.

## Interpretation

- One stream per random operation needs concurrency. For 4 KiB uncompressed
  reads, QD1 delivered 4,373 IOPS and QD16 delivered 23,690 IOPS, a 5.4x gain.
  Writes rose from 4,854 IOPS to 28,561 IOPS between QD1 and QD32.
- QD4 was enough to reach the throughput plateau for 64 KiB and 1 MiB reads.
  More concurrency did not improve those cases and sometimes hurt writes.
- QUIC overhead dominates cached local storage, especially with large reads.
  This is expected: the direct baseline is an in-process memory/page-cache path,
  while QUIC includes framing, encryption, kernel networking, scheduling, and a
  second process.
- Compression did not provide a reliable gain on pseudo-random file data. It
  reduced sequential throughput by 13â€“44% and generally reduced 64 KiB and
  1 MiB random throughput. The 4 KiB cases are below the 16 KiB threshold and
  therefore mostly measure run-to-run noise rather than compression.
- Transparent compression should remain configurable and disabled by default.
  It can help only when network bandwidth is the bottleneck and the payload is
  compressible enough to repay the CPU cost. The threshold is useful for random
  access; sequential streams should use the selected policy for their full
  lifetime.

## Reproduction

The exact two-process commands and all options are documented in the
[benchmark README](../../TeeForge.QuicBench.Shared/README.md). The uncompressed
run wrote `artifacts/quic-file-benchmark/full/results-none.json`; the matched
compression run wrote
`artifacts/quic-file-benchmark/full-compressed/results-fastest.json`. The
`artifacts` directory is intentionally ignored, so this curated record is the
retained evidence.
