# Use immutable differencing state records

Before the first format release, revise the physical-metadata portion of the
VHDX-style differencing decision to store one append-only, checksummed,
allocation-block-sized state record per published BAT transition. The record
atomically contains the VHDX-numbered BAT value and that block's 4 KiB presence
bitmap, while redundant generation roots select state and dependent-registry
tails. This retains the agreed parent identity, grain behavior, erased state,
alignment, durability ordering, and downstream-only I/O while removing the
dynamic format's in-place region table and redo journal from the simpler child
format. Compaction uses transitional roots and retained payload sources so every
injected write-failure boundary remains openable, accepting append growth between
compactions in exchange for a smaller recovery surface.
