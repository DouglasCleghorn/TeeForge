# Use a bounded erasure-stripe redo journal

ErasureCodeStream updates existing data and parity shards in place. A crash between
those member writes could otherwise leave a stripe containing data and parity from
different generations. The format therefore reserves part of a small checksummed
redo ring on every member stream. Its records must remain recoverable with any
supported number of unavailable members; the precise journal coding layout is a
separate format decision.

For each partial-stripe transaction, the journal records the set and configuration
generation, stripe number, transaction sequence, affected ranges, and the after-image
bytes for every touched data range and corresponding parity range. The complete
record and its commit marker are flushed to a write quorum before any described
home-location write begins. That durable marker commits the transaction and requires
replay after a crash. After the home writes reach the required quorum and are flushed,
a checkpoint makes the transaction reclaimable. Replay is idempotent and applies
committed transactions before ordinary reads or writes are allowed; torn,
uncommitted records are ignored because no home write may begin for them.

The journal is deliberately bounded and retains no general write history. Space is
reused only after the associated home writes are durable. If the next transaction
does not fit, the writer first drains committed work and advances the ring. Large
aligned full-stripe writes may use a separately specified safe path, but they must not
bypass crash consistency merely because they are large.

This is distinct from the dynamic allocation stream's metadata-only journal. The
erasure journal includes payload and parity after-images because metadata alone cannot
repair a stripe whose members contain different data generations. The cost is extra
write traffic and flush barriers; the benefit is fixed member placement without a
copy-on-write allocation map or garbage collector.
