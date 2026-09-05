# Use a durable frontier for in-place erasure reshape

An in-place erasure reshape persists a reshape intent containing the source and
target extent geometries, conversion direction, aligned conversion quantum,
and committed conversion frontier. Complete target-layout stripes govern the
converted side of the frontier and complete source-layout stripes govern the
unconverted side. Foreground reads and writes route to the authoritative
layout for their logical range and briefly serialize only with the quantum
currently being converted.

Each conversion quantum aligns to complete stripes in both geometries. The
implementation reads and validates its source, durably records sufficient redo
information, writes and flushes the target representation, verifies it, and
only then atomically advances the frontier. Recovery completes or replays a
journaled quantum before exposing the stream. Widening conversions proceed in
the overlap-safe forward direction and shrinking conversions in the reverse
direction; an operation whose mapping or journal requirement cannot be proven
safe is rejected in favor of disjoint set migration.

Ordinary member loss pauses the reshape before another quantum begins, while
ambiguous corruption faults the stream. This protocol accepts temporary mixed
geometry and more complicated address translation in exchange for resumable
online conversion without requiring storage for a complete second copy.
