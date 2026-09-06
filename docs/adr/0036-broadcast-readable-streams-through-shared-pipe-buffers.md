# Broadcast readable streams through shared pipe buffers

Group BroadcastPipe, its options and reader failure policy, BroadcastStream,
and the copy extensions in TeeForge.Broadcasting. Move the pipe helpers into
TeeForge.Broadcasting.Internal. This replaces the earlier TeeForge.Pipelines
feature namespace from ADR 0022: its only public family serves broadcasting,
so keeping it separate would split one feature across two imports.

BroadcastStream will own a source pump and a fixed list of independent Stream
readers. It is a coordinator rather than a Stream subclass: a single Read method
cannot express independent multicast cursors. The Stream suffix identifies the
stream-oriented broadcast facility and its BroadcastHashStream specialization.

Reuse BroadcastPipe's pooled segment chain, per-reader cursors, backpressure, and
reclamation. Read source bytes directly into writer memory and adapt each reader
to Stream. A small endpoint wrapper tracks consumed Position, rejects overlapping
reads, coordinates disposal with active reads, and reports a source error after
the reader drains its published prefix. Shared payload storage avoids one queue
per reader; callers still receive bytes in their own read buffers.

The asynchronous pump starts at construction. Buffer options bound outstanding
unread data with pause/resume thresholds, allowing less than one source-read
quantum of overshoot plus allocation/segment overhead. Consumer work must progress
concurrently; an abandoned reader must be disposed. Disposing every reader stops
the pump. The owner cancels and awaits the pump before reclaiming its endpoints
and owned source; it cannot force cancellation on arbitrary caller-owned sources.

BroadcastHashStream reuses the existing hash destinations and completion
coordinator to observe each source chunk once, inline before publication. Reader
positions do not enter the hash contract. Results publish only at successful
source EOF, rather than on disposal as TeeHashStream does: a prefix abandoned
during reading must not be presented as a completed full-source digest. EOF means
the end of the source from its initial position. Hash completion need not wait for
every reader to consume the buffer. Hash resources are released on every pump exit.

Source faults and cancellation are retained on Completion and surfaced by each
reader after draining published bytes. Disposal reports cleanup failures without
rethrowing a previously reported pump failure. Dynamic subscriptions, replay,
seeking, per-reader hashes, and lossy slow-reader policies are outside this API.
