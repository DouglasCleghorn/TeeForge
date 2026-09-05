# Support read-only open with an in-memory journal overlay

Opening requires a readable, seekable backing stream. Write capability is
optional. A non-writable backing stream forces read-only mode, and options can
force the same mode even when the backing stream is writable. Creation requires
a readable, writable, seekable backing stream.

Read-only mode reports CanWrite as false and rejects logical writes, Trim, and
compaction. It does not start background free-space discovery because no new
physical allocation can consume discovered holes. Compaction-savings estimation
remains available because it does not mutate the format or scan payload data.

An active journal does not prevent read-only recovery. Valid redo records are
replayed into an in-memory word overlay, and allocation metadata reads consult
that overlay without modifying the backing stream. Disposal discards the
overlay. A later writable open performs ordinary durable replay.

The dynamic allocation stream remains logically usable as a sequential source:
CopyTo and CopyToAsync read from logical position zero to the persisted logical
end without the caller seeking. The backing stream must nevertheless be
seekable because BAT, trim, region, and payload locations are physically
nonsequential after ordinary allocation and compaction.
