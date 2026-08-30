# TeeForge differencing stream format 1.0

## Scope

This document is the normative version-1.0 specification for `.tfdiff` images.
A conforming implementation presents the image as a seekable logical stream by
overlaying child state on an independently supplied base stream. The format is
inspired by VHDX differencing disks but is not byte-compatible with VHDX.

The words **must**, **must not**, **required**, **should**, and **may** are used
in their RFC 2119 sense.

## Geometry and chain invariants

- The logical sector and presence-grain size is exactly 4096 bytes.
- Logical grain zero always describes logical bytes 0 through 4095. Physical
  headers do not shift logical grain numbering.
- Every physical structure starts at a 4096-byte-aligned offset. Payload and
  record blocks start at an allocation-block-aligned offset.
- Allocation block size is a power of two from 64 KiB through 256 MiB.
- `VirtualCapacity` is positive, immutable, and a multiple of 4096 bytes.
- Every member of a TeeForge chain has the same `VirtualCapacity`, block size,
  and 4096-byte logical geometry.
- Reads are bounded by `Length` and capacity. A write or trim whose exclusive
  end exceeds capacity fails before physical I/O.

## Identity and parent binding

Each image stores an immutable nonzero `Id` and a nonzero `DataWriteId`.
`DataWriteId` changes durably before the first caller-visible logical mutation
performed during each writable open. Compaction and dependent registration do
not change it.

A child stores the base `Id` and `DataWriteId` observed at creation. Open must
reject a base whose current pair differs, using
`DifferencingStreamBaseMismatchException`. A TeeForge parent must also match the
stored capacity and block size. Validation occurs at create and open, not before
each I/O; mutating a base while a child is open violates the ownership contract.

An optional UTF-8 relative parent path is a locator hint only. A consumer must
validate identity and geometry after resolving any hint or catalog candidate.
`DifferencingStream.ReadLocator` and `ReadLocatorAsync` validate the identifier
checksum and expose the recorded parent identity, geometry, and hint without
opening a base or changing the supplied stream's position.

## Checksums and byte order

All integers are little-endian. GUIDs use RFC 4122/network byte order. Each
checksummed structure stores an XXH64 checksum at bytes 8 through 15. The
checksum covers the complete structure with those eight bytes treated as zero.
Unused and reserved bytes must be zero.

## Header allocation block

Physical allocation block zero contains the identifier at offset 0, root A at
4096, and root B at 8192. The remainder of the allocation block is zero. This
keeps all logical payload origins independent of header size and leaves room for
future compatible header structures.

### Identifier

The identifier is exactly 4096 bytes:

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 8 | ASCII `TeeDIF\r\n` |
| 8 | 8 | checksum |
| 16 | 2 | major version |
| 18 | 2 | minor version |
| 20 | 4 | allocation block size |
| 24 | 16 | image ID |
| 40 | 16 | initial data-write ID |
| 56 | 16 | base ID |
| 72 | 16 | base data-write ID |
| 88 | 8 | virtual capacity |
| 96 | 4 | UTF-8 parent-hint byte length |
| 100 | 60 | reserved zero |
| 160 | variable | parent-hint bytes followed by zero padding |

The identifier is immutable after creation. The live data-write ID is selected
from the current root.

### Redundant roots

Each root is exactly 4096 bytes:

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 8 | ASCII `TeeDRoot` |
| 8 | 8 | checksum |
| 16 | 8 | generation |
| 24 | 16 | image ID |
| 40 | 16 | current data-write ID |
| 56 | 16 | base ID |
| 72 | 16 | base data-write ID |
| 88 | 2 | major version |
| 90 | 2 | minor version |
| 92 | 4 | allocation block size |
| 96 | 8 | virtual capacity |
| 104 | 8 | cached logical length |
| 112 | 8 | state-record tail offset, or zero |
| 120 | 8 | registry-record tail offset, or zero |
| 128 | 3968 | reserved zero |

Open validates both roots and selects the valid root with the greater generation.
Equal-generation roots with different content are corruption. If only one root
is valid, it is authoritative. Publishing a root writes the parity-selected root
sector, makes it durable, and only then changes the in-memory current root.

## Immutable metadata records

Version 1 uses append-only immutable record blocks instead of in-place BAT and
presence regions. This is the deliberate simplification from the dynamic format:
a payload becomes durable, then one checksummed record atomically publishes its
BAT value and presence bitmap, then a redundant root publishes the new tail.
Unreachable appended blocks are harmless and later reclaimed by compaction.

### State record

A state record occupies one allocation block:

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 8 | ASCII `TeeDSt\r\n` |
| 8 | 8 | checksum |
| 16 | 8 | previous state-record offset, or zero |
| 24 | 8 | logical block index |
| 32 | 8 | BAT value |
| 40 | 4 | presence-bitmap byte length |
| 44 | 20 | reserved zero |
| 64 | variable | presence bitmap followed by zero padding |

The bitmap length is `ceil((BlockSize / 4096) / 8)`. Bit zero describes the
first 4096-byte grain of this logical block. Open walks records newest to oldest;
the first valid record for each logical block is authoritative. Every chain must
terminate, every record must be aligned and checksummed, and live payload and
metadata ownership must not overlap.

### Dependent-registry record

A registry record occupies one allocation block:

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 8 | ASCII `TeeDDep\n` |
| 8 | 8 | checksum |
| 16 | 8 | previous registry-record offset, or zero |
| 24 | 16 | immediate-child ID |
| 40 | 1 | action: zero unregisters, one registers |
| 41 | remainder | reserved zero |

Open walks newest to oldest and applies only the first action for each ID.
Registration and unregistration are idempotent. Compaction writes one canonical
registration record per live ID.

## BAT entries

The low three bits of a 64-bit BAT value encode state. The remaining bits are
zero or an allocation-block-aligned payload offset.

| Code | Name | Meaning |
| ---: | --- | --- |
| 0 | inherited | no child payload; read from the base |
| 2 | erased | no payload; synthesize zero and never consult the base |
| 6 | fully present | payload offset is valid; every byte comes from child |
| 7 | partially present | payload offset is valid; presence bits select grains |

Codes 1, 3, 4, and 5 are invalid. Inherited and erased values contain no offset.
Present values contain one nonzero aligned payload offset owned by exactly that
logical block.

For a partially present block, a set bit selects the child grain and a clear bit
selects the base. Presence is monotonic from base-selected to child-selected.
When all logical grains are present, the entry should be promoted to fully
present.

## Logical reads and length

`Length` is the block-aligned end of the highest block live after applying child
state to the base, capped by `VirtualCapacity`. An erased block masks its base
block and is not live itself. Open recomputes length from the selected state and
the block-rounded base length; the cached root value is advisory.

Reads dispatch by BAT state. A partial read may split at 4096-byte boundaries
and issue independent child and base reads. Missing bytes from a short base are
zero. Position-independent operations do not observe or change `Position`.

## Writes and publication order

Ordinary differencing I/O must never write the base.

The first write to an inherited block allocates a zeroed child payload without
copying the complete base. Only affected 4096-byte grains are materialized.
Boundary grains use read-modify-write so bytes outside the caller range retain
their inherited value.

The first partial write to an erased block allocates and durably zeroes a whole
payload, applies the write, and publishes it as fully present. For every newly
selected payload, bytes become durable before its state record, and the state
record becomes durable before its selecting root. An overwrite of an already
selected child byte uses the caller's later flush as its durability boundary.

## Trim

Trim uses an absolute logical range, does not change `Position`, never extends
`Length`, and never reveals base data:

- a complete block becomes erased;
- a complete aligned 4096-byte grain is zeroed in child storage and selected;
- an unaligned boundary grain is read, modified only over the requested bytes,
  written to child storage, and selected.

Tail trim may reduce `Length`. `SetLength` is unsupported.

## Advisory dependent registry

The registry stores immediate known child IDs. It may be stale, does not prevent
writes, and does not change `DataWriteId`. When `NotifyBaseOnCreate` is requested,
creation first validates that the immediate base exposes a writable registry.
The child is formatted and made durable before its ID is registered upstream. A
registration failure leaves a valid child and is reported to the caller.

No other creation, open, read, write, trim, flush, or compaction operation writes
the base.

## Compaction

Fast compaction writes a canonical metadata snapshot, publishes it through a
temporary root, copies payload only into safe holes while retaining every
currently selected source, publishes packed metadata through another root, and
then truncates unreachable storage. Each intermediate root is independently
openable after failure. Slow compaction first converts logically all-zero child
blocks to erased; it must not discard a nonzero block merely because it equals
the base.

Compaction does not change data identity. Estimation counts canonical live
payload and record blocks without reading payload and therefore does not include
additional slow-mode zero discoveries.

## Compatibility

Major version 1 readers reject other major versions. A reader may open a newer
minor version read-only only when every selected structure and BAT code is
understood; writable open requires the implemented minor version. No TeeForge
disk format had been released when version 1.0 was revised, so prototype images
have no compatibility or migration guarantee.
