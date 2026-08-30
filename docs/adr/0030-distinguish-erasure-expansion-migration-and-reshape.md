# Distinguish erasure expansion, migration, and reshape

`ErasureCodeStream` uses three explicit mechanisms for changing storage rather
than treating every change in member count as one reshape operation.

Capacity expansion appends a separately described allocation extent and
atomically increases logical length without rewriting an existing extent. A
set migration copies logical contents into a separately formatted, disjoint
target set and leaves the source authoritative until the target is complete
and validated. An in-place reshape re-encodes existing logical ranges in
overlapping member storage using bounded durable recovery state.

These mechanisms expose materially different space, availability, and rollback
properties. Keeping them distinct prevents a nominal capacity increase from
silently starting a full-set rewrite, while still allowing callers with an
independent target to choose the simplest recovery model and callers without
duplicate capacity to choose a journaled in-place conversion. `SetLength`
remains unsupported; expansion is an asynchronous maintenance operation whose
final configuration commit changes `Length`.
