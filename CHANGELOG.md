# Changelog

All notable changes to TeeForge are documented here.

## [Unreleased]

Initial release (0.1.0), pending publication.

- Add lazily cached `Base64Url` (unpadded) and `Base32` (uppercase, padded)
  encodings to `TeeHashResult`.

- Add hash-returning `CopyToAsync` overloads for one or many destinations and
  explicit single or multiple hash algorithms, sharing the broadcast copy options.
- Add multi-destination `Stream.CopyToAsync` extensions with shared buffering,
  independent destination progress, stop/continue failure options, and indexed
  aggregate failure reporting. Caller-owned streams remain open and unflushed.
- Add buffered `BroadcastStream` with independent readable endpoints, shared
  pooled storage, slow-reader backpressure, and explicit source ownership.
- Add `BroadcastHashStream` to compute one ordered set of cryptographic hashes
  and checksums for the broadcast, publishing only after successful source EOF.

- Focus TeeForge on ordinary stream composition and optional positional I/O.
- Keep ErasureStream headerless; callers supply geometry and member order.
  Persistent storage prototypes are no longer part of the TeeForge package.
- Add a forward-only encoding/recovery example and core-only build solution.
- Add process-isolated benchmark sampling with raw/aggregate CSVs and source provenance.
- Add `ReplicaStream`, a write-only, forward-only stream that replicates writes
  and flushes across unique writable destinations with concurrent async fan-out,
  configurable synchronous dispatch, deterministic aggregate failures, and
  explicit ownership options.
- Add `RandomAccessMemoryStream`, a `MemoryStream`-compatible in-memory source
  with thread-safe explicit-offset reads and writes, position preservation, and
  independent bounded range streams.
- Add `MigratingStream`, a foreground-prioritized live storage migration
  wrapper with prefix-aware reads, source-first mirrored writes,
  `ITeeRandomAccessStream` support, cancellation and failure fallback, and
  explicit destination handoff and optional source truncation. Add
  `HandoffStream.MigrateAsync` to atomically install the live migration wrapper
  at start and the destination at successful finish, restoring the source on
  failure or cancellation.
- Add `HandoffStream`, a stable serialized endpoint that hands operations off
  to a caller-supplied stream with the same final destination, including
  `ITeeRandomAccessStream` support through native or serialized seek fallback.
- Add `MutualQuicConnection` and `MutualQuicConnectionListener` with local PEM
  identities, mandatory peer-certificate pinning, dynamic `NamedQuicStream`
  pairs, opener-selected transparent Brotli compression, and named
  `QuicRandomAccessChannel` services using threshold-compressed independent
  request streams.
- Add directional `MultipathSenderStream` and `MultipathReceiverStream` data
  paths with dynamic membership, RAID 1, RAID 0, Reed-Solomon erasure coding,
  automatic mirrored fallback, and an optional reliable control channel.
- Bound multipath receive queues and reorder memory, preserve frames across
  read timeouts and cancellation, and wake pending reads on disposal. Add
  atomic sender status and checked control-message payloads, serialize
  completion calls, and fault interrupted group publication. Document current
  operation, ownership, outage, and wire-format contracts separately from proposals.

- Organize public APIs into shallow feature namespaces and name the
  random-access capability family `ITeeRandomAccessStream`,
  `ITeeRangeReadSource`, and `TeeRandomAccess`.
- Add `TeeStream`, a RAID-1-like stream that mirrors operations across an
  arbitrary set of destinations and checks successful results for consistency.
- Add `BroadcastPipe` in `TeeForge.Broadcasting`, a fixed-reader broadcast
  pipeline that retains one pooled copy while each active reader consumes the
  complete byte sequence independently.
- Add `TeeBufferedStream`, adapted from Microsoft's .NET 10 `BufferedStream`,
  with one lazy shared buffer before mirrored `TeeStream` fan-out.
- Add write-only `TeeHashStream` with explicit ordered algorithms, mixed
  cryptographic and non-cryptographic hashing through `TeeHashAlgorithm`, the
  .NET interoperability overloads using `HashAlgorithmName`, and atomic immutable
  results published during disposal. Both input forms share `TeeHashResults` and `TeeHashResult`,
  with `TeeHashAlgorithmId` keys supporting lookups through either identifier.
- Add `TeeBufferedStreamOptions`, an immutable child of `TeeStreamOptions` that
  owns the shared buffer size for `TeeBufferedStream` and `TeeHashStream`.
- Add the MIT-licensed .NET 10 `System.IO.Hashing` package as the sole runtime
  NuGet dependency for incremental checksums and hashing.
