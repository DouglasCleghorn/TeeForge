# ErasureImage

**Experimental; not ready for production use. API and format compatibility are not guaranteed.**

`ErasureImage` adds a persistent member format to the core erasure stream. It maps
one fixed-length logical stream onto `k` systematic data members and `m` parity
members without a stripe journal. Use `ErasureCodedVolume` when an interrupted
random overwrite must be recovered transactionally; use `ErasureImage` when
the caller accepts non-atomic member writes or the members are forward-only.

## Basic use

```csharp
using TeeForge.Experimental.Storage.ErasureCoding;

Stream[] members = OpenSixMemberStreams();
await using ErasureImage stream = ErasureImage.Create(
    members,
    dataShardCount: 4,
    parityShardCount: 2,
    logicalLength: source.Length);

await source.CopyToAsync(stream);
await stream.CompleteAsync();
```

The 128 KiB default member block was selected by the retained
[4+2 local-file experiment](../../../benchmarks/TeeForge.Benchmarks/Experiments/2026-08-26-erasure-stream-block-size.md).
It can be overridden from 4 KiB through 1 MiB in powers of two. The matched
[`RandomAccessMemoryStream` experiment](../../../benchmarks/TeeForge.Benchmarks/Experiments/2026-08-26-erasure-stream-memory-block-size.md)
recommends 64 KiB for memory-backed or random-write-heavy sets.

## I/O model

- A member block is `BlockSize` bytes. One codeword covers `k * BlockSize`
  logical bytes and stores one member block on each of the `k + m` streams.
- The code is systematic: an available data member serves normal reads directly.
  Reed-Solomon reconstruction is used only when the requested data member is
  absent.
- Seekable members expose ordinary seeking plus `ReadAt` and `WriteAt`. Writes
  to the same codeword serialize through one cache entry; different codewords
  may proceed concurrently.
- Random reads populate that same bounded cache. Sequential reads schedule the
  configured number of following logical blocks as read-ahead.
- Non-seekable members support forward reading or writing. A writer must provide
  exactly the declared logical length and call `CompleteAsync` to encode a final
  partial codeword and flush the members.

A random partial write is not atomic across members. A crash can leave the data
and parity for that codeword inconsistent. This is the deliberate boundary
between `ErasureImage` and the journaled implementation.

## Optional self-description

The default `SelfDescribing` format reserves an aligned metadata area before
the member payload. Identical 4 KiB superblocks are stored at offsets 0 and 4096;
the payload begins at `DataOffset`, aligned to the configured block size. Each
hashed superblock contains:

- format and feature versions;
- immutable set ID;
- configuration ID and generation;
- this member's ID and position;
- data/parity counts and codec/layout IDs;
- block size, physical record size, payload offset, logical length, and promised
  alignment; and
- every member ID in codeword-position order.

`ErasureImageHeaderParser.Parse` and `TryParse` inspect a caller-supplied 4 KiB
page. `Read`, `TryRead`, and `ReadAsync` inspect a member stream, preserve the
position of seekable streams, and accept one valid duplicate if the other copy
is damaged.

Set `ErasureImageOptions.Format` to `Raw` to omit all metadata. Raw members
start with payload byte zero and can be reopened with the core
`TeeForge.ErasureCoding.ErasureStream.Open` plus externally supplied geometry
and member order. Prefer the core stream directly for headerless use.

## Availability and maintenance

`RequireAllMembers` defaults to `true`. Setting it to `false` permits a set to
open with any `k` readable members. A degraded set is read-only: writes become
available only after every configured position is present again.

Seekable sets can:

- reconstruct a missing parity member with `ReplaceParityImageAsync`;
- append one parity member with `IncreaseParityAsync`; and
- remove trailing parity members with `ReduceParityAsync`.

Parity growth writes the new parity payload and updates all member headers; it
does not rewrite existing member payloads. The current systematic Vandermonde
codec gives existing parity positions stable rows as parity count grows. Parity
maintenance requires random access and is excluded from concurrent foreground
I/O.
