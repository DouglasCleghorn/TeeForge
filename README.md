# TeeForge

TeeForge is a high-performance .NET 10 library for mirrored streams and
one-writer/many-reader broadcast pipelines.

> TeeForge is pre-release software. Version 0.1 is intended for evaluation and
> audit before the repository and package are made public.

## TeeStream

`TeeStream` presents multiple streams as one RAID-1-like mirror. Capabilities
are exposed only when every destination supports them, and successful results
are checked for consistency by default.

```csharp
using TeeForge;

byte[] payload = [1, 2, 3, 4];
await using var primary = new MemoryStream();
await using var mirror = new MemoryStream();
await using var stream = new TeeStream(primary, mirror);

await stream.WriteAsync(payload);
await stream.FlushAsync();
```

Use `TeeStreamOptions` to select primary-wins mismatches, fault-on-mismatch,
concurrent synchronous fan-out, or `LeaveOpen` ownership.

## TeePipe

`TeePipe` broadcasts every flushed byte to a fixed set of independent readers
while retaining one pooled payload copy.

```csharp
using System.IO.Pipelines;
using TeeForge;

var pipe = new TeePipe(readerCount: 3);
byte[] payload = [1, 2, 3, 4];

await pipe.Writer.WriteAsync(payload);
pipe.Writer.Complete();

foreach (PipeReader reader in pipe.Readers)
{
    ReadResult result = await reader.ReadAsync();
    // Process result.Buffer here. Each reader receives the full payload.
    reader.AdvanceTo(result.Buffer.End);
    reader.Complete();
}
```

The slowest active reader controls writer backpressure. A completed reader
leaves the active set, and `FlushResult.IsCompleted` becomes true only after the
final reader completes.

## Build

```text
dotnet restore --locked-mode
dotnet build --no-restore
dotnet test --no-build
dotnet pack src/TeeForge/TeeForge.csproj --no-build
```

See [the specification](docs/specification.md), [architecture decisions](docs/adr),
and [benchmark records](docs/benchmarks) for the complete behavior and rationale.

## License and provenance

TeeForge is MIT licensed. Its `TeePipe` state machine is adapted from the
MIT-licensed .NET runtime `System.IO.Pipelines.Pipe` implementation pinned in
`THIRD-PARTY-NOTICES.txt`.
