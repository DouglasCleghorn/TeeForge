# Serialize and coalesce erasure notifications per registration

Each `ErasureCodedVolume` notification registration owns one serialized drain
loop, so its handler never overlaps itself and observes retained events in
order. Pending health transitions coalesce to the newest snapshot; maintenance
start and terminal events are retained while intermediate progress may
coalesce. Unregistration prevents new delivery but does not wait for an
already-running handler.

This keeps slow observer code away from member I/O completion paths without
allowing an unbounded callback backlog. Observer exceptions are isolated from
storage and counted only in volatile diagnostics, accepting that an observer
may skip intermediate snapshots in exchange for bounded memory and current
state delivery.
