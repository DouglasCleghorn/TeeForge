# ErasureCodeStream

> Status: experimental public prerelease API. Create/open, degraded I/O, crash
> replay, health telemetry, callbacks, and consistency checking are implemented.
> Online repair, replacement, and reshape are not yet implemented.

`ErasureCodeStream` presents a set of seekable member streams as one
fault-tolerant logical stream. It is closer to a RAID-6 controller than a
mirrored `TeeStream`: each logical stripe is split into `k` data shards and
encoded into `m` parity shards. A typical 6+2 set stores six data shards and
two parity shards, and can reconstruct any two missing or unreadable members.

The format is systematic Reed-Solomon over GF(2^8). Data members contain the
original bytes, so healthy reads do not require decoding. Parity is used for
degraded reads and verification. The current consistency check detects damaged
or stale members; automated repair is a later maintenance operation.

## Using the stream

Formatting requires exactly `k + m` unique, empty, readable, writable, seekable
streams. Logical capacity must be a whole multiple of `k * shardSize`.

```csharp
using TeeForge.ErasureCoding;

var options = new ErasureCodeStreamOptions(
    leaveOpen: true,
    journalSlotCount: 4,
    latencySampleRate: 64);

await using ErasureCodeStream volume = await ErasureCodeStream.CreateAsync(
    memberStreams,
    dataShardCount: 6,
    parityShardCount: 2,
    logicalCapacity: 6L * 1024 * 1024 * 1024,
    options: options);

await volume.WriteAtAsync(payload, offset);
await volume.ReadAtAsync(destination, offset);
```

`Open` and `OpenAsync` accept surviving members in any order. At least `k` valid
members from one unambiguous stable configuration are required. Opening scans
the journal and replays committed work before returning. A read-only open
succeeds only when recovery does not require writes.

The logical stream is fixed capacity. `Position`-based and positional methods
are supported, but `SetLength` is not. Logical operations are currently
serialized; member I/O within an operation runs concurrently.

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
reported fault; it never silently chooses a version. Cancellation is honored
until a transaction reaches commit quorum. Once committed, home writes and any
required replay proceed without the caller's cancellation token so the method
cannot abandon a durable decision.

Each shard also carries a stripe generation UUID. This distinguishes stale
but well-formed data from the current stripe. The data region divides
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
public snapshot reports healthy, degraded, unavailable, faulted, and disposed
health, with read-only and read/write capability represented explicitly.

## Observability and maintenance

`GetState` returns an immutable stream and member snapshot. Exact counters cover
bytes, operations, errors, and reconstruction. Successful read and write
latency is deterministically sampled according to `LatencySampleRate`; failures
and flushes are always timed. The snapshot exposes EWMA latency and throughput,
maximum sampled latency, and base-two microsecond histogram buckets, making a
persistently slow member visible without timing every successful operation.

`RegisterStateChangeHandler` queues a function for member or aggregate health
transitions. `RegisterMaintenanceHandler` reports lifecycle and progress.
Observer exceptions are isolated from I/O. Disposing the returned registration
unregisters the function.

`CheckConsistencyAsync` validates every current shard header and every 64 KiB
integrity block. Its options support continuous foreground work, yielding
balanced work, or delayed background work, plus an optional bandwidth ceiling.
The operation releases the logical-operation gate between stripes so foreground
I/O can proceed. It reports and marks missing, stale, or corrupt members but
does not yet heal them.

Configuration generations and reserved metadata space are designed to support
adding capacity, replacing a member, and changing a 6+2 set to 6+4 without
redefining member identity. Those operations require a migration protocol and
are deliberately not claimed by the current implementation.

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

Implemented and covered by unit or end-to-end tests:

- version-1 layout constants, geometry validation, and quorum calculations;
- scalar, AVX2, and SSSE3 Reed-Solomon encoding and reconstruction;
- deterministic codec vectors and degraded-reconstruction tests;
- 4 KiB member-superblock serialization, XXH128 validation, and A/B selection;
- stable configuration records and ordered member-descriptor serialization;
- shard headers with XXH64 integrity-checksum tables and implicit-zero support;
- journal prepare and commit pages with payload and envelope validation;
- ordered journal quorum grouping with conflict and partial-commit detection;
- block-granular replay planning, Reed-Solomon reconstruction, codeword
  verification, and idempotent current-block suppression;
- formatting with a two-phase complete marker and opening members in any order;
- journal scanning, replay execution, durable home writes, and checkpoints;
- fixed-capacity position-based and positional reads and writes;
- degraded reconstruction and conservative-quorum degraded writes;
- committed-write recovery after injected home-write failures;
- immutable state and member-performance snapshots with registered callbacks;
- foreground, balanced, delayed-background, and bandwidth-limited consistency
  checking with progress callbacks;
- a versioned media-format specification and bounded-journal decision record.

Not yet implemented:

- online repair and replacement of a member stream;
- capacity expansion and data/parity reshape;
- persistent maintenance checkpoints and pause/resume;
- end-to-end physical-drive benchmarks beyond the retained in-memory codec
  measurements.

## Design records

- [Version-1 media format](erasure-code-stream-format.md)
- [Bounded redo-journal decision](adr/0016-use-a-bounded-erasure-stripe-redo-journal.md)
- [Retained Reed-Solomon SIMD benchmark](../benchmarks/TeeForge.Benchmarks/Experiments/2026-08-23-reed-solomon-simd.md)
- [Project terminology](../CONTEXT.md)
