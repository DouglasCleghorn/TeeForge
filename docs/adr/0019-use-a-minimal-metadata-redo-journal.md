# ADR 0019: Use a minimal metadata-only redo journal

BAT entries and other high-frequency metadata are updated in place. The format
therefore uses a checksummed redo journal, modeled on the VHDX log ordering, to
make interrupted metadata updates replayable. Payload overwrites are not
journaled.

An update first makes any newly allocated payload storage durable. It then
writes and flushes a complete journal record describing the metadata changes,
applies those changes to their home locations, and flushes the home metadata.
The journal is not discarded or reused until those home writes are durable. An
open with an active journal replays the newest complete valid sequence before
allowing ordinary I/O.

Metadata changes may be coalesced and batched to reduce journal traffic and
flush barriers. This keeps the steady overwrite path free of payload
copy-on-write and avoids logging payload-sized data. The trade-off is that
metadata durability occurs at an explicit flush boundary or when a bounded
journal batch must be committed, rather than after every individual Stream
write.

A read-only open does not reject an otherwise valid stream merely because its
journal is active. It validates and replays the active sequence into an
in-memory map of home offsets to replacement words. All subsequent metadata
reads consult that overlay. The underlying journal remains unchanged, and the
overlay is discarded when the wrapper is disposed.

The journal is a circular area at the 4 KiB-aligned tail of the reserved header
block. Both metadata roots locate it directly; it is not a region-table entry
and is never moved by compaction. The ring contains independent 4 KiB entries.
Each entry contains a magic value, XXH64 checksum, monotonically increasing
sequence number, log identifier, required physical file length, record count,
and packed records. One 16-byte record identifies an aligned 8-byte home offset
and its replacement 64-bit value. Repeated changes to one home word are
coalesced before logging.

Journal length is derived rather than separately configured:
`clamp(blockSize / 4, 16 KiB, 64 KiB)`. This preserves four entries at the
minimum block size while bounding permanently reserved recovery space for large
blocks. The derivation is a format-version detail that can be revisited if a
future journal layout needs different capacity.

Pending records accumulate in memory until Flush, disposal, or journal capacity
forces a batch. A batch first flushes newly allocated payload, writes and
flushes complete journal entries, applies and flushes the home metadata words,
then advances the active redundant header. The 4 KiB framing detects torn log
writes without copying complete 4 KiB home sectors as VHDX does, keeping write
amplification proportional to the number of changed metadata words.

For an arbitrary backing Stream, its synchronous or asynchronous Flush contract
is the durability boundary available to the format. A FileStream receives the
stronger platform disk barrier: synchronous paths call Flush(true), while async
paths flush managed buffers asynchronously and then call
RandomAccess.FlushToDisk on the safe file handle. Guarantees for other Stream
implementations remain contingent on what their Flush implementation makes
durable.
