# Serialize ReadAheadStream foreground operations

Status: proposed design; ReadAheadStream is not implemented in the current package.


ReadAheadStream serializes foreground reads, writes, seeks, and other position-changing operations because its cooperative background reader and multi-range cache share one underlying stream position. Unlike TeeStream, allowing concurrent foreground operations would make logical position and cache coherence ambiguous, so ReadAheadStream trades caller-visible concurrency for deterministic handoff at read-ahead chunk boundaries.
