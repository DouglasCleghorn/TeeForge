# Serialize ReadAheadStream foreground operations

The proposal below covers a writable, adaptive background cache. TeeForge 0.2
implements a read-only window over independent ranges. Its foreground reads
and seeks are serialized. See the [current specification](../specification.md).

ReadAheadStream serializes foreground reads, writes, seeks, and other position-changing operations because its cooperative background reader and multi-range cache share one underlying stream position. Unlike TeeStream, allowing concurrent foreground operations would make logical position and cache coherence ambiguous, so ReadAheadStream trades caller-visible concurrency for deterministic handoff at read-ahead chunk boundaries.
