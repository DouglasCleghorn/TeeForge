# Changelog

All notable changes to TeeForge are documented here.

## [Unreleased]

- Add `HandoffStream`, a stable serialized endpoint that hands operations off
  to a caller-supplied stream with the same final destination, including
  `ITeeRandomAccessStream` support through native or serialized seek fallback.
- Add `MutualQuicConnection` and `MutualQuicConnectionListener` with local PEM
  identities, mandatory peer-certificate pinning, dynamic `NamedQuicStream`
  pairs, opener-selected transparent Brotli compression, and named
  `QuicRandomAccessChannel` services using threshold-compressed independent
  request streams.
- Add the public fixed-capacity `ErasureCodeStream` with self-describing member
  headers, SIMD systematic Reed-Solomon coding, degraded reads and writes,
  bounded journaled stripe updates, committed-write replay, per-member sampled
  performance telemetry, state and maintenance callbacks, and configurable
  consistency checks. Online repair, member replacement, and reshape operations
  remain future work.

## [0.1.0] - 2026-08-23

- Organize public APIs into shallow feature namespaces and name the
  random-access capability family `ITeeRandomAccessStream`,
  `ITeeRangeReadSource`, and `TeeRandomAccess`.
- Add `TeeStream`, a RAID-1-like stream that mirrors operations across an
  arbitrary set of destinations and checks successful results for consistency.
- Add `TeePipe`, a fixed-reader broadcast pipeline that retains one pooled copy
  while each active reader consumes the complete byte sequence independently.
- Add `DynamicAllocationStream`, a sparse block-addressed stream with a
  checksummed version-1 format, redundant generation roots, metadata redo
  journal, block trim, read-only recovery, locality-aware allocation, and fast
  or zero-scanning slow compaction.
- Add `TeeBufferedStream`, adapted from Microsoft's .NET 10 `BufferedStream`,
  with one lazy shared buffer before mirrored `TeeStream` fan-out.
- Add write-only `TeeHashStream` with explicit ordered algorithms, mixed
  cryptographic and non-cryptographic hashing through `TeeHashAlgorithm`, the
  original `HashAlgorithmName` path, and atomic immutable results published
  during disposal.
- Add `TeeBufferedStreamOptions`, an immutable child of `TeeStreamOptions` that
  owns the shared buffer size for `TeeBufferedStream` and `TeeHashStream`.
- Begin the `ErasureCodeStream` version-1 implementation with a documented
  on-media format, bounded stripe redo-journal design, managed scalar/AVX2/SSSE3
  Reed-Solomon codec, checksummed A/B member superblocks, stable configuration
  records, shard headers, journal prepare/commit pages, conservative quorum
  grouping, and block-granular replay reconstruction. The usable stream API
  remains under development.
- Add the MIT-licensed .NET 10 `System.IO.Hashing` package as the sole runtime
  NuGet dependency for XXH128 member-header checksums.

[Unreleased]: https://github.com/DouglasCleghorn/TeeForge/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/DouglasCleghorn/TeeForge/releases/tag/v0.1.0
