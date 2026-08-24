# ErasureCodeStream

> Status: under development. The on-media format and core coding primitives
> exist, but `ErasureCodeStream` is not yet part of TeeForge's public API.

`ErasureCodeStream` will present a set of seekable member streams as one
fault-tolerant logical stream. It is closer to a RAID-6 controller than a
mirrored `TeeStream`: each logical stripe is split into `k` data shards and
encoded into `m` parity shards. A typical 6+2 set stores six data shards and
two parity shards, and can reconstruct any two missing or unreadable members.

The format is systematic Reed-Solomon over GF(2^8). Data members contain the
original bytes, so healthy reads do not require decoding. Parity is used for
degraded reads, repair, and consistency checking.

## Safety model

### Self-describing members

Every member starts with two independently validated 4 KiB superblock copies.
The header records:

- format magic and header version;
- a set UUID and a unique member UUID;
- the member's data-or-parity role and position;
- configuration UUID and monotonically increasing configuration generation;
- geometry needed to locate metadata, journal, and shard regions;
- an XXH128 checksum over the complete header with the checksum field cleared.

The A/B copies make a torn header update recoverable. On open, TeeForge accepts
the newest valid copy and rejects duplicate positions, mixed sets, incompatible
geometry, and ambiguous configurations instead of guessing.

### Crash-consistent stripes

Parity alone does not prevent the RAID write hole: a crash between data and
parity writes can leave individually readable shards from different versions
of a stripe. Version 1 therefore reserves a small, bounded redo journal on each
member.

A stripe update follows this order:

1. write the prepare pages and local after-images, then flush them;
2. write and flush commit pages, and require a valid write quorum;
3. write the new data, parity, and shard headers to their home locations;
4. flush the home writes, and require a valid home-location write quorum;
5. checkpoint the journal transaction before its slot can be reused.

Recovery replays a transaction only when its identity and contents are
validated on a quorum. Incomplete or contradictory evidence produces a
reported fault or read-only state; it never silently chooses a version.

Each shard also carries a stripe generation UUID. This distinguishes stale
but well-formed data from the current stripe. The proposed data region divides
shards into 64 KiB integrity blocks protected by XXH64, allowing reads and
scrubs to identify the damaged ranges that need reconstruction.

## Quorums and degraded operation

For `n = k + m` members, version 1 uses these conservative defaults:

| Operation | Required members |
| --- | ---: |
| Read or reconstruct | `k` valid current shards |
| Commit a write | `max(k, floor(n / 2) + 1)` durable members |
| Import a configuration | A non-contradictory quorum of the newest generation |

Losing up to `m` members can preserve reads. Writes continue only while both
the coding requirement and majority-style metadata quorum are satisfied. The
public stream will report healthy, degraded, read-only, maintenance, and
faulted transitions rather than reducing these conditions to a single Boolean.

## Observability and maintenance

The planned state API includes immutable snapshots plus registered callbacks
for state transitions and maintenance progress. Per-member telemetry will
measure latency, throughput, queued work, errors, reconstructions, and sampled
slow-I/O events so a consistently slow member can be identified over time.
Sampling and aggregation will be configurable where recording every operation
would materially affect throughput.

Consistency checks, repairs, member replacement, and future reshapes will use
a shared maintenance scheduler. Callers will be able to choose the amount of
background concurrency and bandwidth, from paused or idle-only work through
foreground maintenance. Configuration generations and reserved metadata space
are designed to support operations such as adding capacity or changing a 6+2
set to 6+4 without redefining member identity. Those reshape operations are not
part of the first implementation milestone.

## Codec and measured performance

The in-repository codec has a portable scalar implementation that serves as the
correctness oracle and managed x86 SIMD paths for AVX2 and SSSE3. The matrix is
systematic Vandermonde Reed-Solomon, and tests exhaustively reconstruct all
one- and two-member loss combinations for the initial 6+2 geometry.

On a Ryzen 9 5900X, the retained AVX2 encoding experiment measured the
following results for 6+2 encoding. These numbers measure the codec in memory,
not end-to-end member-stream throughput.

| Logical payload | Scalar mean | AVX2 mean | Logical throughput | Allocation |
| ---: | ---: | ---: | ---: | ---: |
| 64 KiB | 924.85 us | 23.59 us | 16.67 GB/s | 0 B |
| 1 MiB | 15.195 ms | 397.04 us | 15.85 GB/s | 0 B |

The implementation is maintained in this repository under TeeForge's MIT
license. It does not introduce a native Reed-Solomon dependency. .NET 10's
MIT-licensed `System.IO.Hashing` package supplies the XXH128 implementation used
by member superblocks.

## Implementation status

Completed foundations:

- version-1 layout constants, geometry validation, and quorum calculations;
- scalar, AVX2, and SSSE3 Reed-Solomon encoding and reconstruction;
- deterministic codec vectors and degraded-reconstruction tests;
- 4 KiB member-superblock serialization, XXH128 validation, and A/B selection;
- a versioned media-format specification and bounded-journal decision record.

Remaining before a public stream API:

- configuration/member descriptors and shard-header serialization;
- journal record serialization, replay, and crash fault-injection coverage;
- member I/O coordination, degraded reads, and durable writes;
- public state, telemetry, notification, and maintenance APIs;
- scrub, repair, replacement, and end-to-end performance tests.

## Design records

- [Version-1 media format](erasure-code-stream-format.md)
- [Bounded redo-journal decision](adr/0016-use-a-bounded-erasure-stripe-redo-journal.md)
- [Retained Reed-Solomon SIMD benchmark](../benchmarks/TeeForge.Benchmarks/Experiments/2026-08-23-reed-solomon-simd.md)
- [Project terminology](../CONTEXT.md)
