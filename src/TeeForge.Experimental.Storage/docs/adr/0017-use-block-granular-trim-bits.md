# Use block-granular trim bits with immediate logical discard

Each trim region is one physical block interpreted as a least-significant-bit-
first bitmap. One bit represents one logical payload block, so a trim region
covers `blockSize * 8` logical blocks. A set bit overrides a nonzero BAT entry:
reads synthesize zeroes without reading the old payload, and the logical block
does not participate in Length.

Trimming an aligned complete allocated block journals its bit without changing
the BAT or payload. Trimming an unallocated block is a no-op. Unaligned boundary
ranges are overwritten with zeroes in place, while fully covered interior
blocks receive trim bits. If trimming removes the highest live blocks, cached
logical Length is recomputed downward immediately.

A complete write to a trim-marked block overwrites its existing physical block,
makes the payload stable, then journals the trim bit clear. A partial write must
first zero the complete physical block, overlay the caller's bytes, make the
payload stable, then clear the bit. If recovery occurs before the clear, reads
still return zeroes; after the clear, no discarded bytes can reappear.

Fast compaction processes every set trim bit by journaling its BAT entry to zero
and clearing the bit, then releasing the physical block after those changes are
durable. This physical reclamation does not change logical behavior or Length a
second time. Slow compaction performs this fast phase first and can additionally
reduce Length when it releases trailing allocated blocks whose payload is all
zero.
