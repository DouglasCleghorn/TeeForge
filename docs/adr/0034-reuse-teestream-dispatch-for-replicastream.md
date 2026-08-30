# Reuse TeeStream dispatch for ReplicaStream

`ReplicaStream` will present a deliberately smaller write-only, forward-only
surface while delegating write, flush, timeout, cancellation, failure, and
ownership mechanics to an internal `TeeStream`. Construction requires every
replica to be writable. The ordinary mismatch policy remains enabled so a
`WriteTimeout` getter still detects inconsistent replica values; writes and
flushes have no successful return value to compare.

This keeps asynchronous start-before-await behavior, synchronous dispatch
selection, all-destination attempts, deterministic failure ordering, and
dispose semantics identical across the two mirroring APIs. Composition rather
than inheritance prevents `ITeeRandomAccessStream`, reads, seeks, length,
position, and set-length from leaking into the replica contract. The tradeoff
is one small wrapper allocation and an internal dispatch indirection.
