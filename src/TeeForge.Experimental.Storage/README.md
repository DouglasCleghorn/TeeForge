# TeeForge.Experimental.Storage

**Experimental research code. Not ready for production use.** Public APIs and
on-disk formats may change without compatibility guarantees. This project is
not included in the TeeForge NuGet package and is not published separately.

This library owns persistent image formats, journals, member identity,
configuration, recovery, and maintenance. It references TeeForge for ordinary
stream composition, positional I/O, and the shared erasure engine. TeeForge has
no dependency on this assembly.

| Namespace and type | Responsibility |
| --- | --- |
| `TeeForge.Experimental.Storage.Sparse.SparseDiskImage` | Sparse allocation, trim, metadata journal, and compaction |
| `TeeForge.Experimental.Storage.Sparse.DifferencingDiskImage` | Persistent parent-bound images and presence metadata |
| `TeeForge.Experimental.Storage.ErasureCoding.ErasureImage` | Self-describing member headers and parity maintenance |
| `TeeForge.Experimental.Storage.ErasureCoding.ErasureCodedVolume` | Journaled stripes, degraded I/O, recovery, and consistency checking |

These types still derive from `Stream`. Their names identify persistent
storage responsibilities beyond ordinary byte-stream behavior.

## Build and test

From the repository root:

```text
dotnet build src/TeeForge.Experimental.Storage -c Release
dotnet test --project tests/TeeForge.Experimental.Storage.Tests -c Release
```

The full `TeeForge.slnx` also includes this library and the Windows mounting
prototype. `TeeForge.Core.slnx` builds the normal library independently.
`IsPackable` is false; CI tests this project separately from core packaging.

## Migration from the earlier prototype

| Previous type in TeeForge | Experimental replacement |
| --- | --- |
| `DynamicAllocationStream` | `Sparse.SparseDiskImage` |
| `DifferencingStream` | `Sparse.DifferencingDiskImage` |
| `ErasureCodeStream` | `ErasureCoding.ErasureCodedVolume` |
| Self-describing `ErasureStream` | `ErasureCoding.ErasureImage` |
| `ErasureStreamHeaderParser` | `ErasureCoding.ErasureImageHeaderParser` |

Add a project reference explicitly and update namespaces and option names.
The move preserves existing format bytes; it does not upgrade stored data.
Headerless members use the core
`ErasureStream.Open(members, dataCount, parityCount, logicalLength, blockSize)`.
Self-describing images must not be opened as headerless streams.

## Research status

The journal algorithms assume backing flushes satisfy their required ordering
and durability barriers. Ordinary stream flushing and transport write
acknowledgements must not be treated as proof of power-loss durability.
Remote durability, interrupted maintenance, repair/replacement, fencing, and
failover require further implementation and failure testing. Online volume
repair and reshape remain unimplemented.

See the [specification](docs/specification.md), [vocabulary](CONTEXT.md),
[sparse format](docs/dynamic-allocation-stream-format.md),
[differencing format](docs/differencing-stream-format.md),
[erasure image](docs/erasure-image.md),
[journaled volume](docs/erasure-code-stream.md), and
[Windows mounting prototype](docs/windows-mounting.md).
