# Keep erasure health, access, and maintenance orthogonal

`ErasureCodeStream` reports storage health as `Healthy`, `Degraded`,
`Unavailable`, or `Faulted`, with `Disposed` as its terminal lifecycle state.
Read-only mode and current read/write capability remain separate snapshot
properties, while maintenance lifecycle is reported through the maintenance
notification surface rather than folded into health.

This avoids a combinatorial status enum in which otherwise identical storage
has different health names because it was opened read-only or is being
scrubbed. Callers must inspect the independent fields they care about, but each
field retains one stable meaning and new maintenance operations do not expand
the health-state machine.
