# Prioritize live I/O during stream migration

`MigratingStream` copies a contiguous prefix in bounded quanta under the same
gate used by foreground operations. It counts queued foreground callers and
does not begin another migration quantum until they have acquired the gate.
The active quantum remains atomic so a read never observes a range as migrated
before its destination write completes.

Writes go to the source first and then the destination while migration is
active. This keeps the source authoritative if destination I/O fails. Writes
are sent to both sides regardless of whether their range has migrated, avoiding
dirty-range tracking and preventing a later copy from restoring stale bytes.
Reads use the destination only within the completed prefix and otherwise use
the source. Successful copy and destination flush atomically make the
destination authoritative.

All position-changing caller operations are serialized because the wrapper has
one logical `Position`. Backing access is position-independent when a native
capability is available and otherwise uses save, seek, operate, and restore
under the gate. This trades concurrent foreground calls for deterministic
position and migration-boundary semantics.

When a HandoffStream initiates migration, it installs a paused MigratingStream
before starting its worker. Successful completion transfers destination
ownership and replaces the wrapper with that destination. Failure transfers
source ownership back and restores it instead. These ownership transfers occur
under the handoff gate before the retired wrapper is disposed, preventing the
wrapper from closing the newly active backing stream.
