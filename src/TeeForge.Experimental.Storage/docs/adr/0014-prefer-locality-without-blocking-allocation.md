# Prefer locality without blocking foreground allocation

Physical allocation uses three tiers. If the preceding logical block is
allocated, its physical successor is preferred when that successor is known to
be free. Otherwise the allocator takes the lowest offset from a bounded queue of
known free physical blocks. If neither source can immediately satisfy the
request, it appends at aligned physical end-of-file rather than waiting for a
free-space search.

The default queue holds at most 4,096 offsets and begins refill below 1,024.
These are references to holes already present in the file, not reserved,
pre-zeroed, or newly allocated blocks; the raw offsets occupy about 32 KiB.
Capacity remains configurable so measurements can tune scan frequency and heap
overhead without changing the file format.

Blocks freed and durably unmapped during the current session enter the queue
immediately. A background metadata scan can discover older holes, but it does
not publish an unknown hole until a complete BAT snapshot proves that no entry
references it. Foreground allocation changes made during the scan are overlaid
on that snapshot. The scan performs bounded metadata reads and yields between
them; failure disables discovery and leaves append allocation available.

An ordinary scanner I/O failure disables only background discovery. Discovering
an invalid BAT alignment, physical bound, metadata overlap, or duplicate owner
instead faults the wrapper and is surfaced by the next public operation. A
suspect physical block is never placed into the free queue.

This policy favors contiguous logical runs and early hole reuse without putting
a whole-file scan on the allocation path. It accepts occasional append growth
while discovery is incomplete and retains only a bounded number of candidates
in the priority queue.

Compaction has two modes. Fast releases trim-marked blocks and packs all
reachable payload and movable metadata blocks. Slow first performs every fast
operation, then scans remaining allocated payload blocks, releases all-zero
blocks, and packs again as necessary. There is no separate combined mode.

Compaction-savings estimation never scans payload for zero content. It computes
the ideal packed physical length from reachable blocks and metadata, including
blocks reclaimable from trim metadata, and subtracts that length from the
current underlying length. Slow compaction can therefore save more than the
estimate if it discovers zero blocks.

If the underlying stream does not support SetLength, compaction still packs the
file. A NotSupportedException from the final truncation is treated as an
unsupported optimization, and the method returns the unchanged physical
length.
