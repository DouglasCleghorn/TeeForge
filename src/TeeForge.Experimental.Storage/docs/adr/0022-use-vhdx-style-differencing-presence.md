# Use VHDX-style differencing presence

The TeeForge differencing format uses fixed 4 KiB presence grains and
VHDX-numbered BAT states for inherited, erased, fully present, and partially
present blocks. Partial presence is published with the BAT value in immutable,
checksummed, allocation-block-sized state records, and every physical structure
retains at least 4 KiB alignment. Trim deterministically masks the base with
zeroes, while a first partial write to an erased block materializes a complete
zero-initialized child block rather than adding a second partial baseline. The
distinct TeeForge format shares the dynamic stream's redundant-root,
payload-ordering, allocation, and compaction principles while keeping its child
metadata recovery surface smaller.

ADR 0032 revises only the unreleased physical metadata representation from
in-place presence regions and a redo journal to immutable state records; the BAT
codes, 4 KiB grain semantics, parent selection, and trim decisions remain.
