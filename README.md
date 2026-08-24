# TeeForge

![TeeForge icon](https://raw.githubusercontent.com/DouglasCleghorn/TeeForge/main/assets/teeforge-icon.png)

High-performance .NET 10 streams for mirrored I/O, buffered fan-out,
multi-hashing, broadcast pipelines, sparse storage, and HTTP range reads.

TeeForge gives ordinary `Stream` and `System.IO.Pipelines` code explicit tools
for sending one byte sequence to multiple destinations, checking that mirrored
sources agree, and addressing large local or remote data without coordinating
through a shared `Position`.

> TeeForge 0.1 is an early release. The public APIs are tested and package
> validation is enabled, but applications should evaluate the library and keep
> independent backups of important data. `ErasureCodeStream` is documented as
> work in progress and is not part of the public API.

## Install

TeeForge targets .NET 10.

```text
dotnet add package TeeForge --version 0.1.0
```

With NuGet Central Package Management:

```xml
<PackageVersion Include="TeeForge" Version="0.1.0" />
```

## What is included

| Namespace and API | Use it for |
| --- | --- |
| `TeeForge.Mirroring.TeeStream` | Mirroring one logical stream across multiple destinations with consistency checks |
| `TeeForge.Mirroring.TeeBufferedStream` | Coalescing logical I/O once before mirrored fan-out |
| `TeeForge.Hashing.TeeHashStream` | Writing to destinations while calculating one or more cryptographic hashes or fast checksums |
| `TeeForge.Pipelines.TeePipe` | Broadcasting one writer's complete byte sequence to a fixed set of independent readers |
| `TeeForge.Sparse.DynamicAllocationStream` | Storing a very large sparse logical stream in an on-demand block layout |
| `TeeForge.RandomAccess.ITeeRandomAccessStream` | Reading or writing at explicit offsets without changing `Position` |
| `TeeForge.RandomAccess.ITeeRangeReadSource` | Opening independent, bounded streams over logical ranges |
| `TeeForge.RandomAccess.HttpRandomAccessStream` | Reading large HTTP resources through resilient byte-range requests |

All shipped public APIs include XML documentation for IntelliSense. The
package is marked as trim-compatible and Native AOT-compatible.

## Quick start: mirror a stream

`TeeStream` presents multiple streams as one RAID-1-like mirror. An operation
is available only when every destination supports it. Successful return values
and read content are checked for consistency by default.

```csharp
using TeeForge.Mirroring;

byte[] payload = [1, 2, 3, 4];
await using var primary = new MemoryStream();
await using var mirror = new MemoryStream();
await using var stream = new TeeStream(primary, mirror);

await stream.WriteAsync(payload);
await stream.FlushAsync();
```

Use `TeeStreamOptions` to select primary-wins mismatches, fault-on-mismatch,
concurrent synchronous fan-out, or `LeaveOpen` ownership. Asynchronous writes
to independent destinations are issued before the phase is awaited, allowing
capable storage to schedule multiple outstanding requests.

## Buffer once, then fan out

`TeeBufferedStream` adapts the .NET 10 `BufferedStream` implementation for an
arbitrary set of mirrored destinations. One lazy shared buffer and a
large-write bypass coalesce logical I/O before `TeeStream` fan-out; it does not
create a separate buffer for each destination.

```csharp
using TeeForge.Mirroring;

await using var primary = new MemoryStream();
await using var mirror = new MemoryStream();
await using var stream = new TeeBufferedStream(
    bufferSize: 16 * 1024,
    primary,
    mirror);

await stream.WriteAsync([1, 2]);
await stream.WriteAsync([3, 4]);
await stream.FlushAsync();
```

As with `BufferedStream`, writes and their failures can be deferred until the
buffer fills, `Flush` is called, or the stream is disposed. Reads, seeks, and
returned values retain `TeeStream` consistency checks.

## Hash while writing

`TeeHashStream` is a write-only `TeeBufferedStream` that adds one internal hash
destination for every selected algorithm. Results remain empty until disposal
finalizes and atomically publishes all immutable hash values.

```csharp
using TeeForge.Hashing;

byte[] payload = [1, 2, 3, 4];
await using var destination = new MemoryStream();

TeeHashResults<TeeHashAlgorithm> hashes;
await using (var stream = new TeeHashStream(
    [TeeHashAlgorithm.SHA256, TeeHashAlgorithm.XxHash3],
    out hashes,
    [destination]))
{
    await stream.WriteAsync(payload);
}

string sha256 = hashes[TeeHashAlgorithm.SHA256].Hex;
string xxHash3 = hashes[TeeHashAlgorithm.XxHash3].Hex;
```

`TeeHashAlgorithm` supports SHA, MD5, CRC, and XXHash families. The original
cryptographic-only `HashAlgorithmName` API remains available, and
`TeeHashAlgorithmAdapter` converts standard cryptographic identifiers between
the two APIs.

Hashes describe the bytes observed by their internal destinations. A buffered
retry after a partial mirrored failure is therefore hashed again.

## Broadcast through pipelines

`TeePipe` broadcasts every flushed byte to a fixed set of independent readers
while retaining one pooled payload copy.

```csharp
using System.IO.Pipelines;
using TeeForge.Pipelines;

var pipe = new TeePipe(readerCount: 3);
byte[] payload = [1, 2, 3, 4];

await pipe.Writer.WriteAsync(payload);
pipe.Writer.Complete();

foreach (PipeReader reader in pipe.Readers)
{
    ReadResult result = await reader.ReadAsync();
    // Process result.Buffer. Every reader receives the complete payload.
    reader.AdvanceTo(result.Buffer.End);
    reader.Complete();
}
```

The slowest active reader controls writer backpressure. A completed reader
leaves the active set, and `FlushResult.IsCompleted` becomes true only after
the final reader completes.

## Store sparse logical data

`DynamicAllocationStream` is a sparse, block-addressed virtual stream over one
seekable backing stream. Logical blocks are allocated on first write,
unwritten gaps read as zero, full-block trim is metadata-only, and compaction
can pack live blocks toward the beginning of the backing stream.

```csharp
using TeeForge.Sparse;

await using var backing = new FileStream(
    "disk.tdas",
    FileMode.CreateNew,
    FileAccess.ReadWrite,
    FileShare.None);
await using var sparse = DynamicAllocationStream.Create(backing);

sparse.Position = 4L * 1024 * 1024 * 1024;
await sparse.WriteAsync([1, 2, 3, 4]);
await sparse.FlushAsync();
```

The default block size is 1 MiB and can be selected at creation. Two
generation-numbered roots, XXH64 checksums, and a metadata redo journal make
interrupted metadata commits recoverable. Payload and allocation-table blocks
are updated in place, so callers choose their own crash-durability flush
boundaries. Read-only open can recover through an in-memory journal overlay.

See the
[version-1 media format](https://github.com/DouglasCleghorn/TeeForge/blob/main/docs/dynamic-allocation-stream-format.md)
for the exact persistence contract.

## Read at offsets and over HTTP ranges

`ITeeRandomAccessStream` adds explicit-offset reads and writes without changing
a stream's `Position`. `ITeeRangeReadSource` opens an independent, bounded,
forward-only stream over a larger logical range. `TeeStream`,
`TeeBufferedStream`, and `DynamicAllocationStream` expose these capabilities
when their destinations or backing stream can support them.

`HttpRandomAccessStream` is a read-only leaf for large HTTP resources. It sends
one range request for each exact positional read or opened range stream, keeps
the response body streaming, validates the opened representation when the
server supplies a validator, resumes interrupted bodies, and shares 429/503
slowdown windows across concurrent requests. The supplied `HttpClient` remains
caller-owned.

```csharp
using TeeForge.RandomAccess;

using var client = new HttpClient();
await using var remote = await HttpRandomAccessStream.OpenAsync(
    client,
    new Uri("https://example.test/movie.mkv"));

byte[] header = new byte[4096];
byte[] index = new byte[16 * 1024];
await Task.WhenAll(
    remote.ReadAtAsync(header, 0).AsTask(),
    remote.ReadAtAsync(index, remote.Length - index.Length).AsTask());

await using Stream window = await remote.OpenReadRangeAsync(
    offset: 64L * 1024 * 1024,
    length: 4L * 1024 * 1024);
```

## Erasure-coded storage status

TeeForge contains internal, tested building blocks for a seekable
RAID-6-like `ErasureCodeStream`: a versioned media format, systematic
Reed-Solomon coding, SIMD implementations, checksummed member headers, and a
bounded redo-journal design. The public stream, degraded I/O, maintenance
scheduler, state notifications, and performance telemetry are still under
development and are not part of version 0.1's public API.

Read the
[ErasureCodeStream design](https://github.com/DouglasCleghorn/TeeForge/blob/main/docs/erasure-code-stream.md)
and
[version-1 media format](https://github.com/DouglasCleghorn/TeeForge/blob/main/docs/erasure-code-stream-format.md)
for the current status.

## Documentation

- [Behavioral specification](https://github.com/DouglasCleghorn/TeeForge/blob/main/docs/specification.md)
- [Architecture decisions](https://github.com/DouglasCleghorn/TeeForge/tree/main/docs/adr)
- [Benchmark evidence](https://github.com/DouglasCleghorn/TeeForge/tree/main/docs/benchmarks)
- [Changelog](https://github.com/DouglasCleghorn/TeeForge/blob/main/CHANGELOG.md)

## Feedback

Use
[GitHub Issues](https://github.com/DouglasCleghorn/TeeForge/issues)
for bug reports, feature requests, and documentation problems. Include the
TeeForge version, target framework, operating system, and a minimal reproduction
when reporting a defect.

## Build from source

```text
dotnet restore TeeForge.slnx --locked-mode
dotnet build TeeForge.slnx -c Release --no-restore
dotnet test --project tests/TeeForge.Tests/TeeForge.Tests.csproj -c Release --no-build --no-restore --minimum-expected-tests 1
dotnet pack src/TeeForge/TeeForge.csproj -c Release --no-build --no-restore
```

Maintainers can follow the
[release checklist](https://github.com/DouglasCleghorn/TeeForge/blob/main/docs/releasing.md).

## License and provenance

TeeForge is available under the
[MIT License](https://github.com/DouglasCleghorn/TeeForge/blob/main/LICENSE).
`TeePipe` and `TeeBufferedStream` are adapted from MIT-licensed .NET runtime
implementations. The sole runtime NuGet dependency, `System.IO.Hashing`, is
also MIT licensed. Exact sources and versions are recorded in
[THIRD-PARTY-NOTICES.txt](https://github.com/DouglasCleghorn/TeeForge/blob/main/THIRD-PARTY-NOTICES.txt).
