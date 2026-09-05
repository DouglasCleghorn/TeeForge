# Use a self-describing header block and chained region tables

Physical block zero is permanently reserved for format and recovery metadata.
The first 4 KiB contains an immutable file identifier beginning with the eight
bytes `TeeDAS\r\n`. Two independently checksummed 4 KiB roots follow. The roots
duplicate the format version, stream identifier, block size, logical length,
and journal state, and use monotonically increasing generations. The primary
region table follows the roots and the circular journal occupies the aligned
tail of the block.

All integers use little-endian encoding. Stream identifiers use the RFC 9562
network-byte-order representation rather than the mixed-endian byte layout of
Guid.ToByteArray. Block sizes are powers of two from 64 KiB through 256 MiB and
default to 1 MiB.

Region tables contain fixed 32-byte entries. An entry identifies its kind,
flags, logical region index, and absolute block-aligned physical offset, with
reserved space for compatible extension. BAT and trim coverage is derived from
the kind, block size, and logical region index. The final physical slot of every
table is reserved for a sub-region-table pointer, which is zero until the table
is extended. Region tables have their own identifier and XXH64 checksum.

Opening validates the identifier and both roots, selects the newest valid root,
replays its active journal, follows the region-table chain, and indexes BAT and
trim locations in memory. This adds bounded open-time work but keeps region
table traversal off the logical read and write paths.
