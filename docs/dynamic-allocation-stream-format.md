# TeeForge dynamic allocation stream format 1.0

Status: accepted for implementation on 2026-08-23.

## Conventions

All offsets and lengths are bytes. All integers are unsigned little-endian
unless a field is explicitly described as a signed .NET `long`. GUIDs use the
RFC 9562 network-byte-order representation. Reserved bytes must be written as
zero and ignored when reading.

Every XXH64 value uses seed zero. The checksum field itself is treated as zero
while its containing structure is hashed. Checksums detect accidental
corruption; they are not authentication.

The configurable block size is a power of two from 64 KiB through 256 MiB,
inclusive, and defaults to 1 MiB. Physical block zero is reserved. Every other
payload or metadata block begins at a positive block-size-aligned absolute
offset. A BAT value of zero is therefore an unambiguous unallocated marker.

The maximum logical address is `long.MaxValue - 1`. Logical length is zero for a
new stream and otherwise the end of the highest live logical block, rounded to
block size and capped at `long.MaxValue`. Sparse gaps below logical length read
as zero. SetLength is unsupported.

## Physical layout

Block zero contains all bootstrap and recovery structures:

| Offset | Length | Structure |
| ---: | ---: | --- |
| 0 | 4 KiB | File identifier |
| 4 KiB | 4 KiB | Root A |
| 8 KiB | 4 KiB | Root B |
| 12 KiB | variable | Primary region table |
| `blockSize - journalLength` | `journalLength` | Metadata journal |

`journalLength` is `clamp(blockSize / 4, 16 KiB, 64 KiB)` and is always a
multiple of 4 KiB. The primary region table ends where the journal begins.

Blocks after block zero may contain payload, BAT, trim bitmap, or sub-region
table data in any order. Allocation normally prefers physical adjacency, then
known early holes, then aligned end-of-file. Physical objects never overlap.

## File identifier

The 4 KiB file identifier is immutable after creation.

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 8 | Signature: `54 65 65 44 41 53 0D 0A` (`TeeDAS\r\n`) |
| 8 | 8 | XXH64 of this 4 KiB structure |
| 16 | 2 | Major version: 1 |
| 18 | 2 | Minor version: 0 |
| 20 | 4 | Block size |
| 24 | 16 | Stream ID |
| 40 | 4056 | Reserved |

The roots duplicate every field needed to recover when the identifier sector is
damaged. An implementation may open a file with an invalid identifier only when
a valid root supplies matching format identity.

## Redundant roots

Each root occupies 4 KiB and has this layout:

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 8 | Signature: `54 65 65 52 6F 6F 74 0A` (`TeeRoot\n`) |
| 8 | 8 | XXH64 of this root |
| 16 | 8 | Generation; zero is invalid |
| 24 | 16 | Stream ID |
| 40 | 2 | Major version |
| 42 | 2 | Minor version |
| 44 | 4 | Block size |
| 48 | 8 | Cached logical length |
| 56 | 8 | Journal offset |
| 64 | 4 | Journal length |
| 68 | 4 | Journal entry size; 4096 in version 1 |
| 72 | 16 | Active log ID; all zero means clean |
| 88 | 4 | Active log start slot |
| 92 | 4 | Active log entry count |
| 96 | 8 | Active log first sequence |
| 104 | 4 | Next journal slot |
| 108 | 4 | Flags; zero in version 1 |
| 112 | 8 | Required physical length for active replay |
| 120 | 3976 | Reserved |

A root is valid when its signature, checksum, version fields, geometry, and
reserved constraints validate. The valid root with the greatest generation is
current. Equal generations with differing contents, or the absence of any valid
root, is corruption.

A clean root has a zero active log ID, zero active entry count, and zero active
required length. An active root has a nonzero log ID and identifies one complete
journal transaction. Generations advance for activation and again for cleaning.
The inactive root is always overwritten when publishing a newer root.

Cached logical length must be block-aligned unless it is `long.MaxValue`. A
clean open trusts it. Recovery from an active log recomputes it from the replayed
BAT and trim state before publishing a clean root.

## Region tables

The primary region table occupies the variable header range. Each sub-region
table occupies one block. Both use a 64-byte header followed by 32-byte entries.
The final physical entry slot is reserved for the sub-region link and is not
included in ordinary entry count or capacity.

### Region-table header

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 8 | Signature: `54 65 65 52 65 67 6E 0A` (`TeeRegn\n`) |
| 8 | 8 | XXH64 of the used table data |
| 16 | 8 | Zero-based table index |
| 24 | 4 | Ordinary entry count |
| 28 | 4 | Ordinary entry capacity |
| 32 | 32 | Reserved |

Checksum input is the 64-byte header, with its checksum zeroed, followed by all
ordinary entries through `entryCount`, followed by the final sub-region-link
slot. Unused ordinary slots are excluded.

### Region entry

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 4 | Kind |
| 4 | 4 | Flags |
| 8 | 8 | Logical region index |
| 16 | 8 | Absolute physical offset |
| 24 | 8 | Reserved |

Kinds are 1 for BAT, 2 for trim bitmap, and 3 for sub-region table. Flag bit 0
means required; all other version-1 flag bits are zero. BAT and trim entries are
required. The final link slot is either all zero or a required kind-3 entry. Its
logical index is the next table index.

Duplicate `(kind, logical region index)` pairs, duplicate physical ownership,
misaligned offsets, offsets inside block zero, truncated regions, loops, and
nonconsecutive sub-region indexes are corruption.

A BAT region with index `r` covers logical block indexes beginning at
`r * (blockSize / 8)`. A trim region with index `r` covers logical block indexes
beginning at `r * (blockSize * 8)`.

Unknown required kinds prevent opening. A newer minor version containing only
understood required kinds may open read-only. Writable open requires exact
version 1.0 so compaction cannot discard unknown optional data.

## Block allocation table

A BAT region is exactly one block containing `blockSize / 8` consecutive
64-bit absolute physical offsets. Entry zero within region zero maps logical
block zero. A zero value is unallocated. A nonzero value must be block aligned,
must describe a complete physical block outside block zero, and must not point
to known metadata.

BAT entries are updated in place through the metadata journal. Values are
validated before use. Broader duplicate-ownership validation occurs during
background discovery, estimation, recovery scans, and compaction.

## Trim table

A trim region is exactly one block interpreted as a least-significant-bit-first
bitmap. Bit `b` represents logical block
`logicalRegionIndex * blockSize * 8 + b`. A set bit overrides BAT data: the
logical block reads as zero and is not live for logical-length calculation. A
set bit for an unallocated block is noncanonical and is cleared during writable
recovery or compaction.

Aligned full-block trim sets bits without changing payload or BAT. Unaligned
boundary portions are overwritten with zero in place; fully covered interior
blocks receive bits. Trim never extends length, and its complete input range
must lie within current logical length.

A full write to a trimmed block makes the replacement payload stable before
clearing its bit. A partial write first zeroes the complete physical block,
overlays caller data, makes the block stable, then clears the bit. Fast
compaction changes the associated BAT entry to zero and clears the trim bit in
one journal transaction before releasing the physical block.

## Metadata journal

The header-resident journal is a circular array of 4 KiB slots. Each active slot
contains one independently valid entry:

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 8 | Signature: `54 65 65 4C 6F 67 0D 0A` (`TeeLog\r\n`) |
| 8 | 8 | XXH64 of the complete 4 KiB entry |
| 16 | 8 | Sequence; zero is invalid |
| 24 | 16 | Log ID |
| 40 | 4 | Zero-based transaction entry index |
| 44 | 4 | Transaction entry count |
| 48 | 4 | Patch count, at most 252 |
| 52 | 4 | Reserved |
| 56 | 8 | Required physical length |
| 64 | 4032 | Packed patches followed by zero padding |

Each 16-byte patch contains an aligned absolute 8-byte home offset and its
replacement 64-bit value. A transaction coalesces repeated changes to one home
offset. Patch targets may address BAT, trim, or region-table words, but never the
file identifier, either root, or the journal itself.

An active transaction is valid only when every root-referenced slot validates,
wraparound is correct, log IDs match, entry indexes cover the exact range,
sequence numbers are consecutive, required lengths agree, padding is zero, and
all patches pass target validation. Invalid active state is corruption; an
implementation must not guess or partially replay it.

### Commit protocol

One bounded transaction proceeds in this order:

1. Write new payload or metadata blocks and establish required physical length.
2. Execute a physical durability barrier.
3. Write every journal entry.
4. Execute a physical durability barrier.
5. Publish and durably flush a newer active root identifying the transaction.
6. Apply patches to their home metadata locations in ascending offset order.
7. Execute a physical durability barrier.
8. Recompute logical length when liveness may have changed.
9. Publish and durably flush a newer clean root, advancing the next log slot.

If interruption occurs before step 5, the new log is unreachable and ignored.
From step 5 onward, replay is idempotent and completes every patch before a
clean root is published. Journal space is not reused while referenced by the
current root.

Pending changes are coalesced in memory until Flush, disposal, or journal
capacity requires a transaction. A transaction larger than the ring is divided
into recovery-safe batches and is not whole-call atomic.

For FileStream, a durability barrier uses `Flush(true)` or the corresponding
managed-buffer flush followed by `RandomAccess.FlushToDisk`. For another Stream,
the guarantee is relative to that implementation's Flush contract.

### Recovery

A writable open replays the active transaction to home locations, flushes it,
reloads region metadata, scans BAT and trim liveness to recompute logical
length, and publishes a clean root. A read-only open validates the transaction
and stores its patches in a volatile offset-to-word overlay. All metadata reads
consult the overlay; logical length is computed from overlaid state. The file is
not modified.

## Logical I/O

Position may range from zero through `long.MaxValue`. Reads at or beyond Length
return zero bytes. Reads below Length return payload for allocated untrimmed
blocks and synthesize zero for sparse or trimmed blocks.

A nonempty write whose exclusive end would exceed `long.MaxValue` fails before
performing I/O. Writing any byte makes its logical block live and may extend
Length to that block's end. A partial first write initializes every other byte
of the physical block to zero. Ordinary writes to allocated untrimmed blocks are
in-place and may be partially visible after failure or power loss.

Public operations are serialized. The backing stream must be readable and
seekable; creation additionally requires write capability and an empty physical
stream. Open automatically becomes read-only when the backing stream cannot
write, and options can force read-only operation. Exclusive backing mutation is
required for the wrapper lifetime.

## Free-space discovery

Allocation first tries the known-free physical successor of the preceding
logical block, then the smallest offset in the known-free priority queue, then
aligned end-of-file without waiting. The default queue capacity is 4096 offsets
and refill begins below 1024.

A background BAT scan builds a complete physical-ownership snapshot before
publishing newly discovered holes. Foreground allocation deltas are overlaid on
the snapshot. Ordinary scan I/O failure disables discovery and preserves append
allocation. Structural invalidity faults the wrapper and never creates a free
candidate.

## Compaction

Fast compaction first releases all trim bits, then packs every reachable payload
and movable metadata block into the earliest holes. Slow compaction performs the
complete fast phase, additionally releases allocated all-zero payload blocks,
then finishes packing.

A move copies the source to a known-free earlier block, establishes durability,
journals the owning BAT or region-table pointer, applies and flushes that
mapping, and only then considers the source free. Block zero never moves.
Truncation occurs only after every surviving mapping is durable. If the backing
stream's SetLength throws NotSupportedException, packing succeeds without
truncation and returns the unchanged physical length.

Compaction-savings estimation performs metadata math only. It calculates the
ideal packed length of currently live payload and required metadata after trim
reclamation. It never scans payload for zero blocks, so slow compaction may save
more than estimated.

## Public API summary

The version-1 API consists of an unsealed DynamicAllocationStream, immutable
DynamicAllocationStreamOptions, DynamicAllocationCompactionMode with Fast and
Slow values, and DynamicAllocationStreamCorruptionException.

Creation and open have synchronous and asynchronous static forms. Options
contain LeaveOpen, ReadOnly, free-queue capacity, and low-watermark. The stream
exposes Id, BlockSize, IsReadOnly, and UnderlyingStream.

Trim and TrimAsync use absolute logical ranges and do not change Position.
EstimateCompactionSavings and its async form are permitted in read-only mode.
Compact and CompactAsync return resulting physical length. SetLength throws
NotSupportedException.
