# ErasureCodedVolume version-1 format specification

Status: experimental version-1 implementation baseline. Golden serialization
vectors and initial crash-replay tests pass, but the format is not declared
stable for long-term interchange yet.

This document defines the compatibility boundary for `ErasureCodedVolume`. Public
API naming may evolve before implementation, but an implementation must not write
different bytes while identifying them as media format version 1.

## Goals and boundaries

Version 1 provides a readable, writable, seekable fixed-capacity logical stream
over `k` systematic data members and `m` parity members. It supports degraded
reads and writes while quorum remains, detects torn and stale shard updates, and
recovers committed updates with a bounded redo journal.

Version 1 reserves extensible metadata records for consistency checks, member
replacement, capacity expansion, and parity expansion. The initial implementation
may expose consistency checking and healing before online reshape. It must not
publish a new stable configuration until every logical range is readable under
that configuration; an interrupted maintenance operation leaves the preceding
stable configuration authoritative.

The format does not claim stronger persistence than its member streams provide.
Every durability barrier described below means successful completion of the
configured member flush operation. Hardware power-loss safety additionally
requires each member implementation to make that flush durable.

## Required member capabilities and ownership

Formatting requires every member to be non-null, unique by object reference,
empty, readable, writable, seekable, and flushable. Opening requires readable,
seekable streams; write operation and committed-journal replay additionally
require a writable quorum. All members must permit positioning and length
queries.

The supplied order is not persistent identity. Each formatted member carries a
random member identifier and its current position. Opening accepts members in any
order and rejects two streams claiming the same member identifier.

The stream owns members unless `LeaveOpen` is selected. The current implementation
serializes logical caller operations so stripe generation selection and updates do
not race. Internal reads and writes to different members run concurrently.

## Numeric and checksum conventions

- All integers are unsigned little-endian unless explicitly stated otherwise.
- UUIDs use the RFC 9562 network byte order, not `Guid.ToByteArray()` mixed-endian
  order.
- Reserved bytes are written as zero and ignored when reading version 1.
- Header and record hashes use Microsoft `System.IO.Hashing.XxHash128` with the
  hash field zeroed.
- Integrity-block hashes use Microsoft `System.IO.Hashing.XxHash64` over exactly
  the stored block bytes.
- Hashes are converted with `HashToUInt128` or `HashToUInt64` and the resulting
  unsigned integer is stored little-endian. Raw `Hash(...)` byte-array ordering
  is not part of the format.
- A hash mismatch invalidates the containing header, record, journal fragment, or
  integrity block. XXHash is an integrity checksum, not an authenticity mechanism.

## Erasure code

The version-1 codec identifier is `1`, systematic Reed-Solomon over GF(2^8). It
uses primitive polynomial `0x11D`. The first `k` matrix rows are the identity;
the remaining rows are produced by constructing a Vandermonde matrix for all
`k + m` rows and multiplying it by the inverse of its top `k` rows. Member
positions `0..k-1` are data and positions `k..k+m-1` are parity.

Valid configurations have `2 <= k`, `1 <= m`, and `k + m <= 255`. SIMD and scalar
backends must produce identical bytes. Media records carry the codec identifier so
a future codec can coexist without changing header version 1.

## Quorums

For `n = k + m`:

```text
read quorum  R = k
write quorum W = max(k, floor(n / 2) + 1)
```

The majority term prevents disjoint writers in symmetric or parity-heavy sets.
A read requires `R` mutually compatible shard sources for one stripe generation.
A journal transaction commits only after `W` valid commit fragments are durable.
Journal space becomes reclaimable only after `W` valid home shard records for the
new generation are durable.

Members that missed a committed update become stale. Stale and unavailable members
both consume resiliency. A set can therefore become unreadable with only `m` members
currently absent if additional present members contain old stripe generations.

## Member physical layout

Each member has the following top-level layout:

| Offset | Length | Contents |
| ---: | ---: | --- |
| `0x0000` | 4096 | Member superblock A |
| `0x1000` | 4096 | Member superblock B |
| `0x2000` | configurable | Metadata record region |
| aligned after metadata | configurable | Member portion of stripe journal |
| `DataOffset` | `StripeCount * ShardRecordSize` | Shard records |

Metadata and journal lengths are multiples of 4096. `DataOffset` is aligned to
the larger of 4096 and the shard size. The initial defaults are a 4 MiB metadata
region and four journal slots. A journal slot occupies
`AlignUp(8192 + ShardSize, 4096)` bytes on each member: one prepare page, at most
one shard of after-image payload, and one commit page.

The shard size is a power of two from 64 KiB through 16 MiB and defaults to 1 MiB.
The integrity-block size is fixed at 64 KiB in version 1. Consequently every shard
contains between 1 and 256 integrity blocks.

All members in one stable configuration use the same `DataOffset`, shard size,
shard-record size, and stripe count. Extra trailing member capacity is ignored
until a later configuration explicitly consumes it.

## Member superblocks

Each 4096-byte superblock has this layout:

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 8 | Magic: `54 65 65 45 43 0D 0A 1A` (`TeeEC\r\n`, SUB) |
| 8 | 2 | Header version: `1` |
| 10 | 2 | Header length: `4096` |
| 12 | 4 | Feature flags |
| 16 | 8 | Superblock generation |
| 24 | 16 | Erasure-set UUID |
| 40 | 16 | Member UUID |
| 56 | 16 | Stable configuration UUID |
| 72 | 8 | Stable configuration generation |
| 80 | 2 | Member position |
| 82 | 2 | Data-member count `k` |
| 84 | 2 | Parity-member count `m` |
| 86 | 2 | Journal slot count |
| 88 | 4 | Shard size |
| 92 | 4 | Integrity-block size |
| 96 | 8 | Stripe count |
| 104 | 8 | Logical capacity |
| 112 | 8 | Metadata-region offset |
| 120 | 8 | Metadata-region length |
| 128 | 8 | Journal offset |
| 136 | 8 | Journal length |
| 144 | 8 | Data offset |
| 152 | 4 | Shard-header size: `4096` |
| 156 | 4 | Shard-record size |
| 160 | 8 | Stable configuration-record offset |
| 168 | 4 | Stable configuration-record length |
| 172 | 4 | Member-state flags |
| 176 | 16 | Stable configuration-record hash |
| 192 | 16 | Superblock hash |
| 208 | 3888 | Reserved zero bytes |

The superblock hash covers all 4096 bytes with bytes 192 through 207 zeroed.
Formatting assigns independent random set and member UUIDs. Configuration UUIDs
are also random; the numeric generation provides ordering and the UUID prevents
unrelated records with a coincident generation from being combined.

Creating a set requires all `k + m` members to format, flush, and verify
successfully. Quorum operation begins only after the initial stable configuration
exists; a partially formatted new set is not opened as a degraded empty set.

An update writes and flushes the older or invalid superblock copy with generation
one greater than the currently selected copy. The previous valid copy remains the
fallback. Opening a member selects its valid copy with the greatest generation.
Set-wide configuration selection then requires matching configuration UUID,
generation, record hash, and geometry from a read quorum. The set is writable
only when that configuration also has write quorum; a lone member with a higher
value cannot select a configuration.

## Stable configuration records

The metadata region is an append-only sequence of 4096-aligned typed records.
Every record begins with the common `TeeECMET` envelope below. Unknown records
marked noncritical are skipped; an unknown critical record makes the member
unsupported rather than corrupt. Record type `1` is the stable configuration
record.

The configuration record begins with a 256-byte header:

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 8 | Magic: ASCII `TeeECMET` |
| 8 | 2 | Envelope version: `1` |
| 10 | 2 | Record type: `1` |
| 12 | 4 | Total aligned record length |
| 16 | 2 | Configuration-record version: `1` |
| 18 | 2 | Record flags; bit 0 means critical |
| 20 | 2 | Header length: `256` |
| 22 | 2 | Member-descriptor size: `64` |
| 24 | 8 | Metadata-record sequence |
| 32 | 8 | Configuration generation |
| 40 | 16 | Configuration UUID |
| 56 | 16 | Parent configuration UUID, or zero |
| 72 | 16 | Erasure-set UUID |
| 88 | 4 | Configuration flags |
| 92 | 2 | Codec identifier |
| 94 | 2 | Data-member count `k` |
| 96 | 2 | Parity-member count `m` |
| 98 | 2 | Member-descriptor count |
| 100 | 4 | Shard size |
| 104 | 4 | Integrity-block size |
| 108 | 4 | Reserved |
| 112 | 8 | Stripe count |
| 120 | 8 | Logical capacity |
| 128 | 4 | Member-descriptor offset: `256` |
| 132 | 12 | Reserved |
| 144 | 16 | Complete-record hash |
| 160 | 96 | Reserved |

The hash covers the complete aligned record with bytes 144 through 159 zeroed.
Exactly `k + m` member descriptors follow the header, sorted by position:

| Descriptor offset | Size | Field |
| ---: | ---: | --- |
| 0 | 16 | Member UUID |
| 16 | 2 | Position |
| 18 | 1 | Role: `0` data, `1` parity |
| 19 | 1 | Initial state flags |
| 20 | 4 | Feature flags |
| 24 | 8 | Required member length |
| 32 | 32 | Reserved |

A stable record is immutable. Maintenance intent and checkpoint records use other
record types and reference both the source and target configuration UUIDs. The old
stable record remains authoritative until migration completes and a write quorum
publishes superblocks referencing the new stable record. This permits future
member and parity expansion without changing the v1 superblock layout.

## Shard records

For stripe `s`, member `p` stores its shard record at:

```text
DataOffset + s * ShardRecordSize
ShardRecordSize = 4096 + ShardSize
```

The first 4096 bytes form the shard header and the remaining `ShardSize` bytes are
payload. Since both sizes are powers-of-two multiples of 4096, no padding follows.

The shard header layout is:

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 8 | Magic: ASCII `TeeECSHD` |
| 8 | 2 | Header version: `1` |
| 10 | 2 | Header length: `4096` |
| 12 | 4 | Shard flags |
| 16 | 8 | Configuration generation |
| 24 | 16 | Configuration UUID |
| 40 | 8 | Stripe index |
| 48 | 16 | Stripe-generation UUID |
| 64 | 2 | Member position |
| 66 | 2 | Integrity-checksum count |
| 68 | 4 | Integrity-block size |
| 72 | 4 | Stored payload length |
| 76 | 4 | Reserved |
| 80 | 16 | Shard-header hash |
| 96 | `8 * count` | XXH64 integrity-block checksums |
| 2144 | 8 | Monotonic transaction sequence that produced this shard |
| remaining | variable | Reserved zero bytes |

The header hash covers the complete header with bytes 80 through 95 zeroed. Each
integrity checksum covers one complete stored integrity block. The final logical
data shard of the final stripe is zero-padded, and its checksum includes those
stored zeroes. Parity always covers the padded representation.

An all-zero shard header represents the implicit initial all-zero shard with zero
configuration and stripe-generation UUIDs; its payload bytes are not trusted or
read. This permits formatting without writing the entire logical capacity. Once a
stripe receives its first committed update, its current members carry ordinary
valid headers. A member that retains an implicit-zero header is then stale for that
stripe and must be reconstructed before its payload can participate in reads.

The transaction sequence and generation UUID are equal across all shards produced
by one committed stripe transaction. The sequence orders generations after
rotating member failures; the UUID prevents unrelated transactions with a
coincident sequence from being combined. A member with a lower sequence or a
different UUID at the selected sequence is stale. A member with the expected
generation but a failed integrity checksum is corrupt. Neither is silently
combined with current shards.

## Logical addressing

For a stable configuration:

```text
stripeWidth       = k * ShardSize
stripeIndex       = logicalOffset / stripeWidth
offsetWithinStripe= logicalOffset % stripeWidth
dataPosition      = offsetWithinStripe / ShardSize
offsetWithinShard = offsetWithinStripe % ShardSize
logicalCapacity   = StripeCount * stripeWidth <= long.MaxValue
```

`Length` is the fixed logical capacity. Seeking may position at the end but not
beyond it. `SetLength` is unsupported in version 1; capacity changes only when a
new stable configuration is published. Reads and writes may cross stripes and are
split internally without changing the caller-visible byte sequence.

## Partial-stripe updates

A caller write is expanded to complete 64 KiB integrity blocks on affected data
shards. The implementation reads the old versions of those blocks, applies caller
bytes, computes the corresponding parity after-images, assigns a random new stripe
generation UUID, and updates checksums for every changed member block.

The write does not require reading or encoding unaffected shard ranges. A full
stripe write supplies all data after-images directly. The implementation may batch
transactions for multiple stripes into available journal slots, but publication
and replay order follows transaction sequence.

## Journal slots

The journal has at least two fixed slots and defaults to four. Slot sequence
numbers are monotonically increasing unsigned 64-bit values; wraparound is not
supported. Keeping at least two slots ensures that a torn overwrite of the oldest
checkpoint cannot erase the only persistent high-water sequence.

Each member slot contains:

1. A 4096-byte prepare page.
2. Zero or more local after-image ranges, totaling at most `ShardSize` bytes.
3. Unused zero padding.
4. A 4096-byte commit page at the end of the slot.

The prepare page has this layout:

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 8 | Magic: ASCII `TeeECJPR` |
| 8 | 2 | Page version: `1` |
| 10 | 2 | Page length: `4096` |
| 12 | 4 | Transaction flags |
| 16 | 8 | Transaction sequence |
| 24 | 16 | Transaction UUID |
| 40 | 16 | Erasure-set UUID |
| 56 | 16 | Configuration UUID |
| 72 | 8 | Configuration generation |
| 80 | 8 | Stripe index |
| 88 | 16 | New stripe-generation UUID |
| 104 | 2 | Member position |
| 106 | 2 | Range count, at most 128 |
| 108 | 4 | Local payload length |
| 112 | 16 | Local-payload hash |
| 128 | 16 | Prepare-page hash |
| 144 | 2 | Range-descriptor offset: `256` |
| 146 | 2 | Range-descriptor size: `16` |
| 148 | 108 | Reserved |
| 256 | `16 * count` | Range descriptors |
| remaining | variable | Reserved zero bytes |

Each range descriptor contains a 32-bit shard offset, 32-bit length, 32-bit
payload offset, and 32 reserved flag bits. The prepare hash covers the complete
page with bytes 128 through 143 zeroed.

Ranges are sorted, nonoverlapping, integrity-block aligned, and describe the final
bytes for this member's changed shard blocks. An unaffected data member has no
local payload; its existing home blocks are already part of the final codeword.
Parity members carry after-images for every affected offset range.

The commit page repeats the transaction identity and hashes:

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 8 | Magic: ASCII `TeeECJCM` |
| 8 | 2 | Page version: `1` |
| 10 | 2 | Page length: `4096` |
| 12 | 4 | State: `1` committed, `2` checkpointed |
| 16 | 8 | Transaction sequence |
| 24 | 16 | Transaction UUID |
| 40 | 16 | Erasure-set UUID |
| 56 | 16 | Configuration UUID |
| 72 | 8 | Stripe index |
| 80 | 16 | New stripe-generation UUID |
| 96 | 2 | Member position |
| 98 | 6 | Reserved |
| 104 | 16 | Prepare-page hash |
| 120 | 16 | Local-payload hash |
| 136 | 16 | Commit-page hash |
| 152 | 3944 | Reserved zero bytes |

The commit hash covers the complete page with bytes 136 through 151 zeroed. Pages
and payloads whose hashes fail do not count toward quorum. Both states prove that
the transaction committed; `Checkpointed` additionally asserts that home quorum
was durable when that page was written.

The collection of local fragments is itself the final Reed-Solomon codeword for
the affected ranges. Recovery combines journal after-images for changed members
with unchanged data home blocks. Any `k` valid final fragments can therefore
reconstruct a missing local after-image without recursively erasure-coding the
journal as a second data set.

## Transaction ordering

For one transaction:

1. Write every available member's prepare page and local after-image payload.
2. Flush those journal writes.
3. Write matching `Committed` pages and flush them.
4. Do not proceed unless `W` complete committed fragments are valid.
5. Apply new payload blocks and shard headers to every writable member.
6. Flush the home writes.
7. Do not checkpoint unless `W` members now contain valid home shard records for
   the new stripe generation.
8. Replace commit pages with `Checkpointed` pages and flush them. The slot is now
   reusable on members that observed the checkpoint.

Cancellation is honored before journal commit. After step 4, completion or safe
replay takes precedence over cancellation because abandoning the transaction could
leave mixed home generations. Errors after commit are reported only after replay
has been attempted or the stream has entered a state that prevents unsafe access.

## Opening and recovery

Opening performs these steps before ordinary I/O:

1. Validate both superblocks on every supplied member and identify members by UUID.
2. Select one stable configuration supported by write quorum.
3. Classify missing, duplicate, foreign, unsupported, and stale members.
4. Scan the small fixed journal on available configuration members.
5. Group fragments by transaction identity and reject conflicting identities with
   the same sequence.
6. Ignore a prepared group with no valid commit pages because no conforming
   writer began its home writes. One through `W - 1` visible commit pages are
   insufficient evidence: unavailable members could contain the rest of a
   previously committed quorum, so opening stops in a faulted state rather than
   guessing that the transaction was uncommitted.
7. Replay committed groups in sequence order. For each affected range, obtain `k`
   valid final fragments from journal after-images, already-current home blocks,
   or unaffected data home blocks; reconstruct missing fragments and write the
   final payload and shard header to every writable member.
8. Flush replayed home writes and checkpoint the transaction after home quorum.
9. Determine the resulting health state and permit only operations supported by
   the remaining quorum.

Replay is idempotent. A shard header already carrying the transaction's generation
and valid integrity checksums needs no payload rewrite. A committed transaction
that cannot supply `k` valid final fragments leaves the stream faulted and preserves
the journal for inspection; it is never guessed or rolled back.

## Read behavior

A healthy read obtains systematic data directly. Degraded reads fetch member
headers concurrently, group them by stripe generation, and decode only when a
group supplies at least `k` valid fragments. Integrity blocks are verified when
read; failures convert that fragment into an erasure for that operation.

The implementation does not combine different stripe generations merely because
their total count reaches `k`. Successful reconstruction may schedule healing, but
foreground reads do not wait for nonessential maintenance.

## State and notifications

The stream exposes an immutable state snapshot containing:

- overall health: `Healthy`, `Degraded`, `ReadOnly`, `Faulted`, or `Closed`;
- active stable configuration and available read/write quorum;
- every known member's identity, position, and condition;
- active maintenance operation and progress;
- journal occupancy and oldest committed transaction; and
- cumulative and sampled per-member performance counters.

Member conditions include `Online`, `Missing`, `Stale`, `Corrupt`, `Rebuilding`,
and `Retired`. Health and maintenance are orthogonal: a degraded set can also be
scrubbing or reshaping.

Callers register state-change and maintenance-notification callbacks and receive an
`IDisposable` registration. Notifications are ordered per registration, dispatched
away from member I/O completion paths, and may be coalesced when a subscriber falls
behind. Subscriber exceptions cannot fail storage operations and are counted in
diagnostics. Disposal stops future notifications and waits only for storage work,
not arbitrary subscriber code.

## Member performance statistics

Exact lock-free counters track bytes, operations, reconstruction bytes, and errors.
Latency measurement is sampled to avoid a timestamp pair on every member operation.
The default samples one operation in 64 per member; `1` measures every operation and
`0` disables latency sampling. Failures and maintenance operations are always timed.

Each snapshot reports sampled read, write, and flush counts; exponentially weighted
latency and throughput; maximum sampled latency; and fixed logarithmic latency
buckets. Sampling decisions are independent per member but deterministic from its
operation counter, making a persistently slow member visible without adding random
number generation to the I/O path.

## Maintenance

Consistency check verifies headers and integrity blocks and then verifies parity.
Heal reconstructs missing, stale, or corrupt shard blocks into their assigned
members. Reshape prepares a new configuration, migrates data with persistent
checkpoints, and publishes it only after the new configuration is independently
readable. Journal replay is mandatory foreground recovery and is not throttleable.

Other maintenance accepts controls for maximum concurrency, byte rate, optional
IOPS rate, work quantum, yield delay, cancellation, and foreground versus background
scheduling. Defaults use one worker per set and yield between bounded quanta.
Foreground caller I/O has priority over background maintenance. Rate limits apply
per erasure set rather than per member so adding members does not silently multiply
the configured maintenance load.

## Required verification before format stabilization

- Binary golden vectors for both superblocks, stable configuration records, shard
  headers, prepare pages, and commit pages.
- Scalar Reed-Solomon vectors for every supported `k/m` boundary and equivalence
  tests for every SIMD backend.
- Exhaustive single-step interruption tests around each transaction write and flush.
- Recovery tests with every combination of up to `m` unavailable members.
- Rotating-failure tests that distinguish unavailable from returned-but-stale
  members and verify the effective-loss state.
- Corruption tests for every independently checksummed region and integrity block.
- Random seek/read/write model tests against a plain logical byte array.
- Notification isolation and maintenance-throttling tests.
- Benchmarks for full-stripe sequential I/O, 64 KiB random updates, degraded reads,
  journal replay, and per-member sampling rates `0`, `64`, and `1`.
