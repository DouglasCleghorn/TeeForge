# Serialize HandoffStream operations

`HandoffStream` will serialize ordinary stream operations and pipeline
handoffs through one gate. A handoff waits for the active operation, flushes the
outgoing stream, installs the caller-supplied replacement atomically, and then
allows queued operations to continue. The replacement is assumed to reach the
same final destination; the handoff stream neither constructs it nor disposes
the outgoing stream.

This gives each handoff one deterministic boundary in the logical byte
sequence and prevents positional operations from spanning different wrapper
pipelines. It trades concurrent reads and writes through one endpoint for
correct composition over arbitrary `Stream` implementations, which generally
share mutable position and do not guarantee safe concurrent instance use.
Explicit-offset operations use a current native `ITeeRandomAccessStream` when
available. Otherwise, the same gate makes save/seek/operate/restore safe for a
seekable current stream, including `System.IO.BufferedStream`.
