# TeeForge: C# stream composition usage guide

Package: **TeeForge {{VERSION}}**. Target framework: **.NET 10 (`net10.0`)**.
Documentation status: **{{STATUS}}**. Use documentation matching the installed
package version; development documentation may contain APIs not yet on NuGet.

TeeForge copies, replicates, broadcasts, and hashes byte sequences through ordinary
`Stream` and `System.IO.Pipelines` APIs. It also supplies explicit-offset I/O,
headerless erasure coding, live stream handoff/migration, and authenticated QUIC.

## Install and find the API

For a published version, install with `dotnet add package TeeForge --version {{VERSION}}`.
If this version is marked unreleased, use a project reference to
`src/TeeForge/TeeForge.csproj` in a checkout of the repository, or a locally built
NuGet package. An install command in these docs does not establish that the
version has been published.

Public types live in feature namespaces; `using TeeForge;` is insufficient.
Start with the task table and a runnable recipe. For exact overloads, consult the
[public API reference](api-reference.md). The NuGet package includes this guide
at `docs/agent-guide.md` and XML API documentation at `lib/net10.0/TeeForge.xml`.

## Choose the API by task

| Task | API and namespace | Essential behavior |
| --- | --- | --- |
| Copy one source to several destinations | `StreamCopyExtensions.CopyToAsync`, `TeeForge.Broadcasting` | Shared buffering; independent destination progress. [Recipe](recipes/copy.md). |
| Calculate multiple hashes while copying | Hash-returning `CopyToAsync`, `TeeForge.Broadcasting`; results in `TeeForge.Hashing` | Returns complete results only after successful copying. [Recipe](recipes/hash.md). |
| Replicate writes to writable streams | `ReplicaStream`, `TeeForge.Mirroring` | Write-only, forward-only. [Recipe](recipes/replicate.md). |
| Mirror reads and writes with consistency checking | `TeeStream`, `TeeForge.Mirroring` | Capabilities depend on all destinations; primary data drives reads. |
| Buffer once before mirrored I/O | `TeeBufferedStream`, `TeeForge.Mirroring` | Buffering is shared before fan-out. |
| Hash writes while mirroring them | `TeeHashStream`, `TeeForge.Hashing` | Results publish when disposed, not flushed. |
| Broadcast a source to independent readers | `BroadcastStream`, `TeeForge.Broadcasting` | Start all readers concurrently. [Recipe](recipes/broadcast.md). |
| Broadcast and compute one set of source hashes | `BroadcastHashStream`, `TeeForge.Hashing` | Hashes complete at successful source EOF. |
| Broadcast pipe data to fixed independent readers | `BroadcastPipe`, `TeeForge.Broadcasting` | One pooled sequence; each reader maintains its own cursor. |
| Read/write without changing `Position` | `ITeeRandomAccessStream`, `TeeRandomAccess`, `TeeForge.RandomAccess` | Explicit byte offsets; check capabilities. [Recipe](recipes/random-access.md). |
| Open a bounded independent read stream | `ITeeRangeReadSource`, `TeeForge.RandomAccess` | Range reads have their own cursor and length. |
| Read remote HTTP byte ranges | `HttpRandomAccessStream`, `TeeForge.RandomAccess` | Representation validation and bounded retries. |
| Replace a live stream endpoint or migrate its backing | `HandoffStream` / `MigratingStream`, `TeeForge.Composition` | Serialized handoff; migration completion and failure fallback. |
| Encode/recover headerless data/parity streams | `ErasureStream`, `TeeForge.ErasureCoding` | Caller supplies length, geometry, and member order. |
| Use authenticated QUIC or dynamic multipath transport | `MutualQuicConnection`, `MultipathSenderStream`, `MultipathReceiverStream`, `TeeForge.Networking` | Certificate pinning, explicit completion, and transport-specific ownership. |

## Working example: copy and hash

This is the exact source compiled and run by the quickstart project. Its
`RunAsync` method accepts the caller's cancellation token. From a repository
checkout run `dotnet run --project samples/TeeForge.Quickstart -c Release -- hash`.

{{HASH_EXAMPLE}}

`HashAlgorithmName` overloads support existing .NET cryptographic selections.
`TeeHashAlgorithm` also includes CRC and xxHash checksums. Both return the same
`TeeHashResults`; keys implicitly accept either input type. Standard algorithms
such as SHA-256 compare equally through both forms. `TeeHashAlgorithmId.Name`
and `IsCryptographic` describe the identifier. Custom .NET names retain runtime
support; do not assume every identifier has a corresponding enum member. Never
cast the identifier to an enum or parse its name without consulting the API.

## Ownership, completion, and concurrency

| Facility | Completion | Ownership and concurrency |
| --- | --- | --- |
| Copy extensions | Await the returned task | Caller streams stay open and are not flushed. Flush destinations explicitly when required. |
| `ReplicaStream`, `TeeStream`, `TeeBufferedStream` | Await writes/flushes; dispose the wrapper | Destinations are owned by default. Use the relevant options' `leaveOpen: true` to retain them. Do not overlap separate operations on a wrapper. |
| `TeeHashStream` | Dispose or await `DisposeAsync` | Results remain empty until all hash destinations finalize. `Flush` is insufficient; internal hash resources are always owned. |
| `BroadcastStream`, `BroadcastHashStream` | Consume readers and await `Completion` | The pump starts during construction. Start every reader before awaiting the first. Dispose an abandoned reader so it cannot hold back the producer. Owner disposal awaits the pump. |
| Hash-returning copy / broadcast hashes | Successful source EOF; copy also waits for destinations | Source failure, cancellation, or abandonment before EOF does not produce complete source hashes. |

An `await using var` declaration disposes at the end of its scope. To access
`TeeHashStream` results earlier, put the stream in an explicit `await using (...)`
block, then read the results after that block.

## Failures and common mistakes

- Pass distinct, non-null, writable destinations. Copying a source into itself is invalid.
- `source.CopyToAsync(singleStream)` binds to the .NET instance method. Pass a
  destination collection to select TeeForge's multi-destination extension, or
  an explicit algorithm to select its hash-returning extension.
- `BroadcastCopyFailureBehavior.Stop` stops other copies after a destination
  failure. `Continue` lets healthy copies finish but still reports failures in
  an aggregate exception with destination indexes. Neither mode rolls back data.
- A buffered mirrored write retried after partial failure may be hashed again.
  A `TeeHashStream` digest describes bytes observed by its hash destination; it
  does not certify that every mirror ended with identical contents.
- Cancellation is cooperative. A caller-owned source that ignores its token can
  delay shutdown. Always await in-flight I/O before disposing its resources.
- Check `CanRead`, `CanWrite`, `CanSeek`, `CanReadAt`, and `CanWriteAt` as applicable.
  Do not emulate concurrent positional reads by mutating a shared `Position`.
- CRC and xxHash are checksums, not security hashes. MD5 and SHA-1 have broken
  collision resistance. Runtime/platform support can limit SHA-3 and QUIC.
- `TeeForge.Experimental.Storage` is an unpublished research assembly. Its disk
  images, journals, and mount tooling are not part of the TeeForge NuGet API.

## More detail

- [Five runnable recipes](recipes/index.md)
- [Exact public API signatures](api-reference.md)
- [Behavioral specification](specification.md)
- [Replication contracts](replica-stream.md)
- [Erasure streaming contracts](erasure-stream.md)
- [Multipath transport contracts](multipath-stream.md)
- [Repository](https://github.com/DouglasCleghorn/TeeForge)

Examples are copied from compiled source by `eng/update-docs.ps1`. CI rejects
stale generated documentation and executes all five quickstart examples.
