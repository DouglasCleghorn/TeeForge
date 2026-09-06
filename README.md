# TeeForge

![TeeForge icon](assets/teeforge-icon.png)

High-performance .NET 10 streams for live composition, mirrored I/O,
write-only replication, buffered fan-out, multi-hashing, broadcast pipelines,
headerless erasure coding, HTTP range reads, and mutually authenticated QUIC.

TeeForge gives ordinary `Stream` and `System.IO.Pipelines` code explicit tools
for sending one byte sequence to multiple destinations, checking that mirrored
sources agree, and addressing large local or remote data without coordinating
through a shared `Position`.

> TeeForge 0.1.0-rc.1 is a prerelease. The public API may change in subsequent 0.x releases.

## Install

TeeForge targets .NET 10.

```text
dotnet add package TeeForge --version 0.1.0-rc.1
```

With NuGet Central Package Management:

```xml
<PackageVersion Include="TeeForge" Version="0.1.0-rc.1" />
```

## What is included

| Namespace and API | Use it for |
| --- | --- |
| `TeeForge.Composition.HandoffStream` | Inserting wrappers such as `System.IO.BufferedStream` into a live stream pipeline |
| `TeeForge.Composition.MigratingStream` | Moving a live readable and writable byte sequence to a replacement backing stream |
| `TeeForge.Mirroring.TeeStream` | Mirroring one logical stream across multiple destinations with consistency checks |
| `TeeForge.Mirroring.ReplicaStream` | Replicating a forward-only write sequence to multiple writable destinations |
| `TeeForge.Mirroring.TeeBufferedStream` | Coalescing logical I/O once before mirrored fan-out |
| `TeeForge.Hashing.TeeHashStream` | Writing to destinations while calculating one or more cryptographic hashes or fast checksums |
| `TeeForge.ErasureCoding.ErasureStream` | Encoding and decoding a fixed-length sequence across headerless data/parity streams |
| `TeeForge.Pipelines.TeePipe` | Broadcasting one writer's complete byte sequence to a fixed set of independent readers |
| `TeeForge.RandomAccess.ITeeRandomAccessStream` | Reading or writing at explicit offsets without changing `Position` |
| `TeeForge.RandomAccess.ITeeRangeReadSource` | Opening independent, bounded streams over logical ranges |
| `TeeForge.RandomAccess.RandomAccessMemoryStream` | Thread-safe positional I/O over an in-memory byte sequence |
| `TeeForge.RandomAccess.HttpRandomAccessStream` | Reading large HTTP resources through resilient byte-range requests |
| `TeeForge.Networking.MutualQuicConnection` | Mutually authenticated named streams and positional services over one QUIC connection |
| `TeeForge.Networking.MultipathSenderStream` / `MultipathReceiverStream` | Mirroring, striping, or erasure-coding one directional byte stream across changing network paths |

All shipped public APIs include XML documentation for IntelliSense. The
package is marked as trim-compatible and Native AOT-compatible.

## Add buffering to a live stream

`HandoffStream` gives callers one stable `Stream` while allowing a caller to
provide a replacement stream with the same final destination. A handoff waits
for an active operation, flushes the outgoing stream, and then lets queued
operations continue through the replacement.

```csharp
using TeeForge.Composition;

await using var destination = new MemoryStream();
await using var stream = new HandoffStream(destination);

await stream.WriteAsync([1, 2]);
var buffered = new BufferedStream(destination, bufferSize: 16 * 1024);
await stream.HandoffAsync(buffered);
await stream.WriteAsync([3, 4]); // Buffered without replacing `stream`.
await stream.FlushAsync();
```

The outgoing stream is not disposed during handoff. The replacement or caller
retains its ownership. Operations and handoffs are serialized so a byte
sequence cannot be split across the old and new streams. `HandoffStream` also
implements `ITeeRandomAccessStream`; native positional I/O is preserved, and a
serialized seek/restore fallback keeps random access available through standard
seekable wrappers such as `BufferedStream`.

## Move a live stream to new storage

`MigratingStream` copies a complete seekable byte sequence from a source to a
destination without taking the logical stream offline. Reads use the migrated
destination prefix or the authoritative source suffix. Writes go source-first
to both backings while migration is active. Each background chunk releases the
operation gate, and any queued caller operations run before the next chunk.

```csharp
using TeeForge.Composition;

await using var source = File.Open("current.bin", FileMode.Open, FileAccess.ReadWrite);
await using var destination = File.Open("replacement.bin", FileMode.Create, FileAccess.ReadWrite);
await using var stream = new MigratingStream(
    source,
    destination,
    new MigratingStreamOptions(bufferSize: 1024 * 1024));

await stream.WriteAtAsync([1, 2, 3], offset: 4096); // Takes priority over the next chunk.
await stream.MigrationCompletion;                   // Destination is now authoritative.
```

Both backings must be distinct readable, writable, seekable streams. Migration
starts at offset zero and does not use or change either backing stream's
`Position`. A migration failure or cancellation leaves the wrapper operating
against the source. The destination becomes the sole backing after successful
copy and flush. Source truncation is explicit through
`truncateSourceOnCompletion`; generic streams cannot delete their underlying
storage. Independent leave-open options control ownership.

An existing `HandoffStream` can own the complete transition. Its current stream
becomes the migration source, the live migrating wrapper is installed before
copying begins, and the destination is installed after successful copy and
flush:

```csharp
await using var live = new HandoffStream(source);
await live.MigrateAsync(destination, new MigratingStreamOptions(
    bufferSize: 1024 * 1024));
```

Reads and writes through `live` remain available throughout. Failure or
cancellation restores the original source. Destination ownership transfers to
`HandoffStream` after success, so its `LeaveOpen` setting controls final
destination disposal. The migration options control whether the retired source
is disposed and whether a partial destination is disposed after failure.

## Quick start: replicate writes

`ReplicaStream` is a write-only, forward-only fan-out stream. Every write and
flush is attempted on every replica; reads, seeks, length, position, and
set-length are deliberately unsupported. Replicas may therefore be pipes,
network request bodies, hash sinks, append-only files, or any other writable
`Stream` without needing compatible read or seek capabilities.

```csharp
using TeeForge.Mirroring;

await using var local = File.Create("local.bin");
await using var remote = await OpenRemoteUploadAsync();
await using var replicas = new ReplicaStream(
    new ReplicaStreamOptions(
        synchronousMode: TeeStreamSynchronousMode.Concurrent,
        leaveOpen: true),
    local,
    remote);

await source.CopyToAsync(replicas);
await replicas.FlushAsync();
```

Async operations begin on every replica before they are awaited. Synchronous
operations run in replica order by default, or concurrently when selected in
`ReplicaStreamOptions`. One failure is rethrown directly; multiple failures use
an index-ordered `AggregateException`. A failed operation is not transactional
and can leave replicas with different prefixes. See
[the ReplicaStream guide](https://github.com/DouglasCleghorn/TeeForge/blob/main/docs/replica-stream.md) for the complete contract.

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

## Read at offsets and over HTTP ranges

`ITeeRandomAccessStream` adds explicit-offset reads and writes without changing
a stream's `Position`. `ITeeRangeReadSource` opens an independent, bounded,
forward-only stream over a larger logical range. `TeeStream`,
`TeeBufferedStream`, `ErasureStream`, and `RandomAccessMemoryStream` expose these capabilities
when their destinations or backing stream can support them.

`RandomAccessMemoryStream` is the in-memory implementation. It has the same
constructor shapes and buffer APIs as `MemoryStream`, serializes positional and
ordinary stream operations, and supports independent bounded range streams.

```csharp
using TeeForge.RandomAccess;

await using var memory = new RandomAccessMemoryStream();
memory.SetLength(1024);

await Task.WhenAll(
    memory.WriteAtAsync(new byte[] { 1, 2 }, 100).AsTask(),
    memory.WriteAtAsync(new byte[] { 3, 4 }, 900).AsTask());
```

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

## Connect streams securely over QUIC

`MutualQuicConnection` authenticates one QUIC connection on which either
endpoint can dynamically open multiple independent `NamedQuicStream` instances.
Every endpoint loads its X.509 certificate and matching unencrypted private key
from local PEM files and pins the peer certificate from another local file. The
TLS 1.3 handshake proves possession of the private key matching that certificate;
a missing, expired, or different peer certificate rejects the connection.

QUIC requires platform support from .NET and its native MsQuic dependency.
Check `System.Net.Quic.QuicConnection.IsSupported` and
`System.Net.Quic.QuicListener.IsSupported` before using these APIs. Windows 11
and Windows Server 2022 or later include the required support through .NET;
Linux requires `libmsquic`. macOS support has additional setup and limitations.
Follow the [.NET QUIC platform prerequisites](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-overview#platform-dependencies).

Trust is based on the exact pinned certificate and its validity dates; it does
not use public certificate-authority trust, hostname validation, or online
revocation checks. Distribute and rotate pins through a trusted channel and
protect the unencrypted private-key files with operating-system permissions.

```csharp
using System.Net;
using System.Net.Security;
using TeeForge.Networking;

var protocol = new SslApplicationProtocol("my-storage-protocol");
var serverOptions = new MutualQuicConnectionOptions(
    "server.crt.pem",
    "server.key.pem",
    "trusted-client.crt.pem",
    protocol);
var clientOptions = new MutualQuicConnectionOptions(
    "client.crt.pem",
    "client.key.pem",
    "trusted-server.crt.pem",
    protocol);

await using var listener = await MutualQuicConnectionListener.ListenAsync(
    new IPEndPoint(IPAddress.Loopback, 0),
    serverOptions);
ValueTask<MutualQuicConnection> accepting = listener.AcceptConnectionAsync();
await using MutualQuicConnection client = await MutualQuicConnection.ConnectAsync(
    listener.LocalEndPoint,
    "localhost",
    clientOptions);
await using MutualQuicConnection server = await accepting;

ValueTask<NamedQuicStream> receiving = server.AcceptStreamAsync();
await using NamedQuicStream clientMetadata = await client.OpenStreamAsync(
    "metadata",
    new NamedQuicStreamOptions(QuicStreamCompression.BrotliFastest));
await using NamedQuicStream serverMetadata = await receiving;
```

The application name is sent once in an uncompressed opening preface; QUIC's
native `Id` identifies the physical stream afterward. Only one live stream pair
may hold a name. The client wins a simultaneous same-name collision, active
duplicates are rejected, and disposing the pair makes the name reusable.

Each named stream is a non-seekable duplex `Stream` and `IDuplexPipe`. One read
and one write can run concurrently while same-direction calls are serialized.
The opener selects transparent `None`, `BrotliFastest`, or `BrotliOptimal`
compression, and the receiver admits it through `AllowedCompressions`. Selected
compression applies to the complete payload in both directions. It has the same
compression engine as manually wrapping with `BrotliStream`; the built-in value
is negotiation and correct duplex/half-close lifecycle management.

Random access is registered separately:

```csharp
server.RegisterRandomAccess("disk", localRandomAccess);
QuicRandomAccessChannel remoteDisk = await client.OpenRandomAccessAsync(
    "disk",
    new QuicRandomAccessOptions(
        QuicStreamCompression.BrotliFastest,
        compressionThreshold: 16 * 1024));
```

The service name is exchanged once for a short connection-local handle. Every
positional operation uses a new independent QUIC stream. Request and response
payloads below the configured threshold remain uncompressed; qualifying
payloads use the negotiated algorithm. Operations are bounded by
`MaximumRandomAccessRequestSize`.

## Encode one stream across several members

`ErasureStream` splits one logical byte sequence into data and parity streams.
Members can be forward-only; seeking and positional I/O are available when the
members support them. It writes no persistent headers or journal.

```csharp
using TeeForge.ErasureCoding;

await using ErasureStream encoded = ErasureStream.Create(
    memberStreams, dataShardCount: 4, parityShardCount: 2,
    logicalLength: source.Length);
await source.CopyToAsync(encoded);
await encoded.CompleteAsync();
```

Keep the logical length, block size, and member order to reopen the sequence.
See [the stream guide](https://github.com/DouglasCleghorn/TeeForge/blob/main/docs/erasure-stream.md) and the runnable
[forward-only example](https://github.com/DouglasCleghorn/TeeForge/blob/main/samples/TeeForge.Streaming/README.md).

## Documentation

- [Behavioral specification](https://github.com/DouglasCleghorn/TeeForge/blob/main/docs/specification.md)
- [Multipath streams: usage, guarantees, and wire format](https://github.com/DouglasCleghorn/TeeForge/blob/main/docs/multipath-stream.md)
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
dotnet restore TeeForge.Core.slnx --locked-mode
dotnet build TeeForge.Core.slnx -c Release --no-restore
dotnet test --project tests/TeeForge.Tests/TeeForge.Tests.csproj -c Release --no-build --no-restore --minimum-expected-tests 1
dotnet pack src/TeeForge/TeeForge.csproj -c Release --no-build --no-restore
```

## License and provenance

TeeForge is available under the
[MIT License](https://github.com/DouglasCleghorn/TeeForge/blob/main/LICENSE).
`TeePipe` and `TeeBufferedStream` are adapted from MIT-licensed .NET runtime
implementations. The sole runtime NuGet dependency, `System.IO.Hashing`, is
also MIT licensed. Exact sources and versions are recorded in
[THIRD-PARTY-NOTICES.txt](https://github.com/DouglasCleghorn/TeeForge/blob/main/THIRD-PARTY-NOTICES.txt).
