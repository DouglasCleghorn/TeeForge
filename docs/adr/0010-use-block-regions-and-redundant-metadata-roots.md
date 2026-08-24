# Use block-sized regions and redundant metadata roots for dynamic allocation

The dynamic allocation stream has a maximum addressable capacity of
`long.MaxValue` and a persisted logical length that begins at zero. Successful
writes extend logical length to the exclusive end of the highest logical block
they touch, capped at `long.MaxValue`. Its configurable block size defaults to
1 MiB. Physical block allocation and metadata growth are lazy, so sparse logical
length does not require payload storage proportional to the address space.
Logical length is a cached value rather than a historical high-water mark. Trim
and zero-block reclamation can lower it when they remove the highest live
logical blocks. Dirty recovery replays the journal and recomputes length from
BAT and trim state; clean opens use the checksummed cached root value. SetLength
is unsupported.

BAT storage and sub-regions occupy whole aligned blocks. Each nonzero BAT entry
is an absolute, block-aligned byte offset in the underlying stream; zero means
unallocated. Region locations use the same absolute-offset convention. A BAT
region is therefore exactly one block rather than a separately configurable
size.

The headers contain two independently checksummed metadata roots with
monotonically increasing generation numbers. Opening selects the valid root
with the greatest generation, leaving the previous root as the recovery point
if root publication is interrupted. BAT entries and payload blocks are updated
in place and therefore are not recovered merely by selecting the older root.

This keeps address translation arithmetic direct and permits metadata to grow
only with allocated address ranges. Block-sized metadata regions can consume
more space than a partially filled variable-size table, and redundant roots add
header writes and recovery logic. Redundancy protects the roots themselves; a
separate recovery protocol is required to make in-place BAT mutation safe.
