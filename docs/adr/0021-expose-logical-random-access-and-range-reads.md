# Expose logical random access and bounded range reads

TeeForge exposes explicit-offset buffer I/O through `ITeeRandomAccessStream` and
independent bounded read streams through `ITeeRangeReadSource`, with both
capabilities operating over each wrapper's logical byte sequence without
changing `Position`.

## Decision

- Keep ordinary `Stream` reads and writes unchanged. Random-access calls are a
  separately discoverable capability and may be serialized internally.
- Adapt known safe leaves, including `FileStream` through
  `System.IO.RandomAccess` and HTTP through byte ranges. Do not publicly adapt
  arbitrary seekable streams with save/seek/restore.
- Propagate a capability through a mirror only when every destination supports
  it. Preserve the existing primary-data and mismatch rules for positional and
  range reads.
- Model a large network reservation as a bounded forward-only response stream,
  not as many small exact range calls. This permits a 4 MiB request to deliver
  its first 12 KiB as soon as those bytes arrive while retaining the remainder
  of the same response for continued playback.
- Keep the HTTP implementation a thin, read-only transport. It owns neither
  the supplied `HttpClient` nor a cache, validates one opened representation,
  resumes interrupted suffixes, coordinates server slowdown windows, and
  aborts child ranges when the parent is disposed.
- Start independent asynchronous I/O within a phase before awaiting the phase.
  This exposes queue depth to capable upstream storage, including devices that
  may use NCQ, without adding a device-specific API or promise.
- Preserve the Microsoft `BufferedStream` control flow initially. Pending
  sequential writes are flushed before a positional read, range open, or
  bypassing positional write. Referencing in-flight buffered writes from a
  positional read is a desirable future optimization but is deferred because
  overlay lifetime and failure ordering are substantially more complex.

## Consequences

Separating buffer operations from leased range streams adds a second public
seam, but it preserves ordinary `Stream` behavior and gives adaptive read-ahead
and erasure layers a uniform logical interface. Callers choose the HTTP
validator policy and retry limits. The range transport does not solve caching,
preloading, or adaptive reservation; those remain later layers that can use one
large progressive range without multiplying HTTP requests.
