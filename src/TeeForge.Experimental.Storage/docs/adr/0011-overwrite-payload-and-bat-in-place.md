# Overwrite allocated payload and BAT entries in place

Dynamic allocation stream writes modify already allocated physical payload
blocks in place. A write to an unallocated logical block initializes a new
physical block before publishing its mapping by updating the corresponding BAT
entry in place. Existing BAT entries are likewise changed in place. Staged
blocks that were not published are unreachable and can later be reclaimed by
compaction.

The committed root generation is not a transaction boundary for payload or BAT
writes. A failed, cancelled, or interrupted write may have modified any already
allocated blocks or BAT entries it reached. A crash can leave a torn payload
overwrite, while a torn BAT overwrite can misdirect a logical block unless a
separate recovery protocol repairs it.

Trim accepts arbitrary byte ranges. Every fully covered logical block receives
a persistent trim bit and immediately reads as zero. Partial boundary blocks are
overwritten in place with the trimmed bytes zeroed.
Fast compaction releases trim-marked physical blocks and clears their BAT
entries. Slow compaction additionally reads allocated blocks and reclaims those
whose complete contents are zero.

In-place payload and BAT writes avoid block-sized copy-on-write amplification,
replacement BAT regions, and temporary payload allocation on the overwrite hot
path. The format relies on the underlying stream for payload overwrite
integrity. BAT integrity requires an additional recovery protocol because the
Stream contract does not guarantee atomic durable 64-bit writes.
