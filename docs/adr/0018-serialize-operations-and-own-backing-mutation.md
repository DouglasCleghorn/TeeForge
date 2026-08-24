# Serialize operations and require exclusive backing mutation

One async-compatible operation gate serializes public DynamicAllocationStream
operations. It protects logical Position, backing-stream seeks, pending journal
state, allocation metadata, and compaction. The background free-space scanner
acquires the same gate only for bounded metadata reads and yields between
chunks.

The wrapper requires exclusive mutation of its backing stream for its complete
lifetime. A caller must not modify the backing stream directly or open another
writable wrapper over the same storage. Read-only wrappers may coexist only
while no writer is active. These rules prevent unjournaled metadata changes and
position races that the format cannot detect reliably.

A partial first write constructs a complete zero-initialized physical block,
overlays the caller's bytes, and makes that complete block stable before
publishing its BAT or trim-table change. A full aligned write bypasses zero
initialization. Partial writes to ordinary allocated, untrimmed blocks continue
to overwrite in place.

Opening validates both roots, the active journal, and the complete region-table
chain. BAT values are validated lazily before use to avoid a full allocation-map
scan on every open. Background discovery, estimation, and compaction validate
the broader BAT ranges they inspect. Invalid alignment, physical bounds,
metadata overlap, or duplicate ownership faults the wrapper rather than
following a suspect mapping.

Serialization adds an uncontended synchronization operation to I/O calls and
prevents parallel reads through one wrapper. Benchmarks must measure this cost,
including small cached operations and large sequential I/O. Any later removal
or partitioning of the gate must retain exclusive access to shared Position,
journal publication, and mutable allocation state.
