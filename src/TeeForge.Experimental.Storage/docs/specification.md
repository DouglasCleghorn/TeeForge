# Experimental storage specification

Not ready for production use. APIs and on-disk formats may change without compatibility guarantees.

## SparseDiskImage

`SparseDiskImage` exposes a sparse logical address space over a single
readable, seekable backing stream. Creation additionally requires an empty,
writable stream and a positive 4 KiB-aligned immutable `VirtualCapacity`.
`Length` is the block-aligned end of the highest allocated, non-trimmed logical
block, is capped by that capacity, and may decrease after trim or compaction.

The creation block size is a power of two from 64 KiB through 256 MiB and
defaults to 1 MiB. A first write allocates and zero-initializes a physical block
unless it overwrites that whole block. Unallocated gaps and trimmed blocks read
as zero. `SetLength` is unsupported.

The block allocation table consists of raw little-endian 64-bit entries: zero
means unallocated and nonzero values are absolute, block-aligned physical
offsets. BAT and trim metadata are allocated in block-sized regions referenced
by a chained region table. The final physical region-table slot is reserved for
the chain link.

Payload and BAT data are written in place. Metadata transitions are protected
by two checksummed, generation-numbered roots and a bounded metadata-only redo
journal in header block zero. Flush establishes the wrapper's durability
boundary. A writable open replays an active valid journal to its home offsets;
a read-only open applies the same patches through an in-memory overlay.

Full-block trim immediately removes a block from logical liveness. Partial trim
zeroes only the requested bytes in place. Fast compaction releases trimmed
blocks and packs live payload and movable metadata toward the start; slow
compaction first performs those operations, then additionally identifies and
releases all-zero payload blocks. `EstimateCompactionSavings` performs only
allocation arithmetic and never scans payload for zeroes.

The exact byte layout, checksum coverage, commit protocol, recovery validation,
allocation strategy, and compatibility policy are normative in
[the SparseDiskImage format specification](dynamic-allocation-stream-format.md).

## DifferencingDiskImage

`DifferencingDiskImage` overlays a writable child stream on a readable, seekable
immediate base. A TeeForge base supplies its stable ID, current data-write ID,
block size, and virtual capacity directly; the explicit overload accepts the
same identity and geometry for another base-stream implementation. Create and
open reject geometry or identity mismatches.

Every member of a chain uses the same immutable 4 KiB-aligned virtual capacity,
allocation-block size, and 4096-byte logical grain origin. The child BAT uses
VHDX-numbered inherited (0), erased (2), fully present (6), and partially
present (7) states. Presence bits select child or parent data independently for
each 4096-byte grain. A small inherited write materializes only affected grains,
while the first partial write after erase starts from a fully zeroed child
block.

Trim never reveals the base. A whole block becomes erased; a partial range uses
4096-byte read-modify-write grains and selects those grains from the child.
Ordinary reads, writes, trim, flush, and compaction never write upstream. The
only optional upstream mutation is `NotifyBaseOnCreate`, which registers the
durable child's stable ID in a writable immediate-base advisory registry.

Both ordinary and explicit-offset synchronous and asynchronous I/O are
serialized per stream. `ReadAt`, `WriteAt`, and their asynchronous forms do not
observe or change `Position`. `DataWriteId` advances durably before the first
logical mutation of each writable open, while child registration does not
change logical data identity.

Standalone sparse images use `.tfdisk`; difference images use `.tfdiff`. The
separate Windows broker and its intentionally explicit driver boundary are
described in [Windows mounting](windows-mounting.md). The precise media contract
is normative in [the DifferencingDiskImage format specification](differencing-stream-format.md).

`ReadLocator` and `ReadLocatorAsync` inspect a child identifier before the base
is resolved. They validate the header checksum, return the recorded parent
identity and geometry plus its optional path hint, and preserve the supplied
stream's position.

## ErasureCodedVolume

`ErasureCodedVolume` is a public fixed-capacity, readable, writable, seekable
`Stream` and `ITeeRandomAccessStream`. `Create` formats exactly `k + m` empty,
unique, readable, writable, seekable member streams. `Open` accepts at least `k`
valid members in any supplied order, discovers their persistent positions, and
replays any committed non-checkpointed journal transaction before returning.
Read-only open is supported when no transaction needs replay.

The implementation serializes logical operations while it issues independent
member I/O concurrently. Positional `ReadAt` and `WriteAt` do not change
`Position`. Capacity is fixed by the stable configuration, so `SetLength` and
seeks beyond `[0, Length]` are rejected. A canceled write is abandoned only
before its commit quorum; after commitment it completes or recovers without
using the caller cancellation token.

`GetState` returns immutable aggregate and per-member snapshots. The member
performance snapshot contains exact byte, operation, reconstruction, and error
counters plus deterministically sampled latency, throughput, maximum latency,
and histogram buckets. `RegisterStateChangeHandler` and
`RegisterMaintenanceHandler` queue observer functions and isolate observer
exceptions from storage operations.

`CheckConsistencyAsync` validates every current header and 64 KiB integrity
block. `ErasureMaintenanceOptions` selects foreground, yielding balanced, or
delayed background scheduling and an optional bytes-per-second limit. The check
reports corrupt, stale, and missing positions but does not yet repair them.
Member replacement, repair, capacity expansion, and parity reshaping are future
operations.

See [the ErasureCodedVolume overview](erasure-code-stream.md) for the safety
model and current status. Its proposed version-1 media format, quorum behavior,
crash recovery, state model, maintenance controls, and verification
requirements are defined in
[the ErasureCodedVolume format specification](erasure-code-stream-format.md).
