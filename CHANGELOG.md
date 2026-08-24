# Changelog

All notable changes to TeeForge are documented here.

## [Unreleased]

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
  Reed-Solomon codec, and checksummed A/B member-superblock serialization. The
  public stream API remains under development.
- Add the MIT-licensed .NET 10 `System.IO.Hashing` package as the sole runtime
  NuGet dependency for XXH128 member-header checksums.

[Unreleased]: https://github.com/DouglasCleghorn/TeeForge/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/DouglasCleghorn/TeeForge/releases/tag/v0.1.0
