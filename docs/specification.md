# TeeForge 0.1 specification

Status: accepted for implementation on 2026-08-22; updated on 2026-08-23.

## Package

- Package ID and assembly name: `TeeForge`.
- Version: `0.1.0`.
- Target framework: `net10.0` only.
- Public namespaces are organized by stable feature family:
  `TeeForge.Mirroring`, `TeeForge.Pipelines`, `TeeForge.Hashing`,
  `TeeForge.RandomAccess`, `TeeForge.Sparse`, and `TeeForge.ErasureCoding`.
- The root `TeeForge` namespace contains no public types. Consumers import only
  the feature families they use.
- License: MIT.
- Public concrete classes remain unsealed.
- The sole runtime NuGet dependency is Microsoft's MIT-licensed
  `System.IO.Hashing`, used for the persisted XXHash checksums.
- The package contains XML documentation, portable symbols, Source Link data,
  README, changelog, license, and third-party notices.

## TeeStream

### Construction and ownership

The public constructors are:

```csharp
TeeStream(params Stream[] destinations)
TeeStream(TeeStreamOptions options, params Stream[] destinations)
TeeStream(IEnumerable<Stream> destinations, TeeStreamOptions? options = null)
```

At least one non-null destination is required. Duplicate object references are
rejected. The input is copied to an internal array and is not publicly exposed.
The first destination is the primary stream. `LeaveOpen` defaults to `false` and
applies to all destinations.

### Capabilities and operations

`CanRead`, `CanWrite`, `CanSeek`, and `CanTimeout` are intersections across all
destinations. Read, write, seek, length, position, timeout, flush, set-length,
and disposal operations fan out where the `Stream` contract permits them.

Separate caller operations are not serialized. Callers retain the usual Stream
single-owner responsibility. Async fan-out operations run concurrently, except
that mirrored reads first require the primary result. Sync operations default
to deterministic destination-index order; `Concurrent` mode fans out all sync
operations where no primary dependency prevents it.

Every operation attempts every relevant destination even after an earlier
failure. One underlying failure is rethrown with its original stack. Multiple
failures are reported in a deterministic-index `AggregateException`.

### Reads

The primary reads directly into the caller's buffer. If it returns a positive
count, every mirror is read until exactly that count has been obtained, using a
rented temporary buffer per mirror, then compared. Legal short-read chunking is
normalized. Async mirror reads run concurrently.

If the primary returns zero for a non-empty request, TeeStream returns zero and
does not probe mirrors. If the primary throws, each mirror receives the original
caller request so every failure can be observed; successful mirrors may advance.

### Mismatches

`TeeStreamMismatchBehavior` has three values:

- `ThrowAndContinue` (default): throw for this mismatch and keep operating.
- `ThrowAndFault`: throw and permanently fault the wrapper after a mismatch.
- `UsePrimary`: expose primary data/results despite successful differences.

Destination exceptions always fail the current operation but do not change
future wrapper state. `ThrowAndFault` applies only to successful-but-inconsistent
results or data.

`TeeStreamConsistencyException : IOException` reports the operation, optional
primary numeric result, and indexed mismatch metadata. Read mismatches record
the first differing offset but never retain or print byte contents.

### Cancellation and disposal

A pre-canceled token invokes no destination. After an async operation begins,
the token is passed to every destination and all operations are awaited. If all
unsuccessful results are cancellation, the operation is canceled; any ordinary
failure produces an aggregate that also contains cancellation exceptions.

Synchronous and asynchronous disposal attempt every owned destination.
`DisposeAsync` fans out concurrently. The wrapper becomes disposed even if one
or more destination disposals fail.

## Logical random access and bounded ranges

`ITeeRandomAccessStream` is the position-independent buffer capability:

```csharp
bool CanReadAt { get; }
bool CanWriteAt { get; }
int ReadAt(Span<byte> buffer, long offset)
ValueTask<int> ReadAtAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default)
void WriteAt(ReadOnlySpan<byte> buffer, long offset)
ValueTask WriteAtAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken = default)
```

Calls operate on a wrapper's logical byte sequence and neither observe nor
modify `Stream.Position`. Concurrent capability calls are safe, although a
wrapper may serialize them to protect logical metadata. Overlapping reads and
writes have no additional snapshot or transaction semantics.

`ITeeRangeReadSource.OpenReadRangeAsync(offset, length, cancellationToken)` returns
an independently owned, read-only, forward-only `Stream`. It is bounded by both
the requested length and the source length. A zero-length request or an offset
at or beyond end of stream returns an empty stream. Range streams let a source
reserve and stream a useful multi-megabyte transfer while a consumer begins
from its small prefix, avoiding one network request for every small read.

`TeeRandomAccess.TryGet` discovers an existing implementation or adapts a
`FileStream` through `System.IO.RandomAccess`. It deliberately does not adapt
an arbitrary `CanSeek` stream. A public save/seek/restore adapter could race an
unrelated owner of the same stream; only wrappers with exclusive ownership may
use such a fallback internally.

`TeeStream` exposes a capability only when every destination exposes it.
Positional reads retain primary-sized consistency checking, positional writes
attempt every destination, and a range open owns one bounded child stream per
destination. Asynchronous fan-out starts all independent destination writes
before awaiting the phase. `DynamicAllocationStream` translates logical
offsets through its BAT and uses an upstream capability for physical I/O when
present. Independent physical reads and durability-safe groups of journal or
metadata writes are submitted together before their barrier. These patterns
permit an upstream device to exploit queueing such as NCQ without defining an
NCQ API or guaranteeing a particular scheduling policy.

`HttpRandomAccessStream` is a caller-`HttpClient`-owned, read-only leaf. Open
uses a `bytes=0-0` GET probe to establish length and range support. Exact
`ReadAt` calls request exactly their bounded buffer range. Range streams keep
the original response body streaming and resume only an unread suffix after a
premature EOF or transport failure. No client-side concurrency cap or cache is
provided by this layer.

The default HTTP representation policy records a strong ETag when available,
otherwise records Last-Modified as a best-effort validator, and otherwise
continues with length and range validation. Configuration may require a strong
validator or disable validator checks. A later 412, changed validator, or
changed total Content-Range faults the source with
`HttpRepresentationChangedException`; optional representation retries continue
to target only the original snapshot. HTTP 429 and 503 responses update one
shared not-before time across all in-flight reads. Defaults are three slowdown
retries, a two-minute maximum requested wait, zero representation-change
retries, three body-resume retries, and 250 ms exponential backoff with jitter.

The HTTP leaf intentionally does not preload or cache. A future adaptive
read-ahead/cache layer should consume `ITeeRangeReadSource`, initially reserve a
large range (the current design target is 4 MiB), expose progressively filled
prefixes, coalesce overlapping readers, and grow its reservation only after
sustained consumption. The future public ErasureCodeStream should likewise
translate this same logical capability at its member-I/O boundary.

## TeeBufferedStream

`TeeBufferedStream` is adapted from Microsoft's `BufferedStream` source pinned
to the same .NET `release/10.0` commit as `TeePipe`. `TeeBufferedStreamOptions`
is an immutable, unsealed child of `TeeStreamOptions` that adds a positive
`BufferSize`, defaulting to 4 KiB. Buffered sequence constructors receive that
complete options object rather than a separate buffer-size argument.

The public constructors are:

```csharp
TeeBufferedStream(params Stream[] destinations)
TeeBufferedStream(int bufferSize, params Stream[] destinations)
TeeBufferedStream(TeeBufferedStreamOptions options, params Stream[] destinations)
TeeBufferedStream(
    IEnumerable<Stream> destinations,
    TeeBufferedStreamOptions? options = null)
```

The direct `int` overload is retained as a `BufferedStream`-style convenience;
it constructs the corresponding options internally. Constructors do not accept
a base `TeeStreamOptions` plus a loose buffer size.

One lazy byte array is shared between logical reads and writes. Microsoft's
large-operation bypass, temporary shadow-buffer heuristic, seek bookkeeping,
sync and async paths, APM compatibility, and copy behavior are retained. When
buffered data is emitted, a TeeStream applies capability intersection,
primary-sized reads, consistency checking, fan-out ordering, aggregate failure
reporting, and ownership to every destination.

Capabilities are sampled when the internal TeeStream is created because the
buffering hot paths query them repeatedly and Stream capabilities conventionally
remain stable while open. The outer stream reports false after disposal.

Writes and their destination failures may be deferred until the shared buffer
fills, the caller flushes, an incompatible read/seek requires a flush, or the
stream is disposed. As in Microsoft's implementation, the shared mutable buffer
is not safe for overlapping synchronous caller operations; asynchronous paths
use the upstream serialization discipline.

The initial random-access extension preserves the adapted Microsoft buffering
control flow. A positional read or range open flushes pending sequential writes
before reading upstream, and a positional write flushes earlier buffered writes
before bypassing the write buffer. A future optimization may allow positional
readers to reference or overlay an in-flight write buffer instead of forcing
that flush, but it is deferred until buffer lifetime, ordering, retry, and
failure visibility can remain compatible with the original implementation.

## TeeHashStream

`TeeHashStream` derives from `TeeBufferedStream` and is write-only. It requires
at least one unique, non-null, writable caller destination, then appends one
internal `HashWriteStream` destination for each configured algorithm.
Cryptographic destinations use `IncrementalHash`; non-cryptographic
destinations use the corresponding `System.IO.Hashing` implementation with its
default seed or parameter set. Every constructor requires an algorithm or
algorithm sequence as its first parameter. A sequence must contain at least one
unique value; unsupported algorithms fail construction before ownership is
taken.

The public constructors are:

```csharp
TeeHashStream(
    HashAlgorithmName algorithm,
    out TeeHashResults results,
    params Stream[] destinations)

TeeHashStream(
    IEnumerable<HashAlgorithmName> algorithms,
    out TeeHashResults results,
    IEnumerable<Stream> destinations,
    TeeBufferedStreamOptions? options = null)

TeeHashStream(
    TeeHashAlgorithm algorithm,
    out TeeHashResults<TeeHashAlgorithm> results,
    params Stream[] destinations)

TeeHashStream(
    IEnumerable<TeeHashAlgorithm> algorithms,
    out TeeHashResults<TeeHashAlgorithm> results,
    IEnumerable<Stream> destinations,
    TeeBufferedStreamOptions? options = null)
```

The `HashAlgorithmName` path remains cryptographic-only. `TeeHashAlgorithm`
contains `MD5`, `SHA1`, `SHA256`, `SHA384`, `SHA512`, `SHA3_256`, `SHA3_384`,
`SHA3_512`, `Crc32`, `Crc64`, `XxHash32`, `XxHash64`, `XxHash3`, and
`XxHash128`. One enum-based call may mix both families. Member documentation
identifies the family, warns that CRC and XXHash are unsuitable for security,
warns about MD5 and SHA-1 collision resistance, and records platform-dependent
SHA-3 availability.

`TeeHashAlgorithmAdapter` publicly converts the eight standard cryptographic
identifiers between `HashAlgorithmName` and `TeeHashAlgorithm`. It exposes
`ToTeeHashAlgorithm`, `TryToTeeHashAlgorithm`, and
`TryToHashAlgorithmName`. Unknown names and undefined enum values fail their
try-conversion. Non-cryptographic enum members also fail conversion to
`HashAlgorithmName`; adapters do not extend the original constructor path with
non-cryptographic names.

Hashing runs inline through the ordinary TeeStream fan-out without worker
threads, queues, or payload copies. Hash destinations preserve TeeBufferedStream
delivery and retry behavior. A digest describes the ordered bytes accepted by
that hash destination, including bytes accepted again when a partial buffered
failure is retried; it does not certify the final state of ordinary mirrors.

`TeeHashResults` implements
`IReadOnlyDictionary<HashAlgorithmName, TeeHashResult>`. Before completion it
has zero entries and `IsComplete` is false. `Flush` does not complete hashing.
`Dispose` and `DisposeAsync` always finalize and dispose internal hash
destinations, regardless of `LeaveOpen`, then atomically publish all results in
configured order. `LeaveOpen` applies only to caller destinations. An ordinary
destination disposal failure is still reported but does not suppress hashes
that finalized successfully. If any hash cannot finalize, the dictionary stays
empty, `IsComplete` stays false, and disposal reports the failure.

Each published `TeeHashResult` is immutable. It exposes its
`HashAlgorithmName`, digest as `ReadOnlyMemory<byte>`, uppercase hexadecimal,
and padded Base64. Text encodings are computed lazily and safely under
concurrent access.

The enum path publishes the equivalent
`TeeHashResults<TeeHashAlgorithm>`, implementing
`IReadOnlyDictionary<TeeHashAlgorithm, TeeHashResult<TeeHashAlgorithm>>`.
The generic result types require an enum key and retain the same immutability,
ordering, lazy encoding, empty-until-complete, and atomic-publication behavior.

## TeePipe

### Construction and API

The public constructors are:

```csharp
TeePipe(int readerCount)
TeePipe(int readerCount, TeePipeOptions options)
```

`readerCount` must be positive. The public endpoints are:

```csharp
PipeWriter Writer
IReadOnlyList<PipeReader> Readers
IReadOnlyList<Task<Exception?>> ReaderCompletions
void Reset()
```

The endpoint lists are immutable. Writer and reader instances are stable across
generations. `Reset` is permitted only after the writer and every reader have
completed. The caller must retrieve `ReaderCompletions` again after reset; a
cached old list continues to describe its original generation.

### Broadcast and ownership

Every reader independently observes the complete flushed byte sequence. The
writer and each individual reader retain the standard single-owner Pipe rules;
distinct readers may operate concurrently.

Payload is stored once in a shared pooled segment chain. Each reader has its own
consumed and examined cursors. One shared lock, adapted from Microsoft Pipe,
protects brief state transitions. No additional locks are introduced and data
processing occurs outside the critical sections.

### Backpressure and reclamation

The active reader with the greatest unexamined byte count controls pause and
resume. The writer pauses when any reader reaches `PauseWriterThreshold` and
resumes only after all readers fall below `ResumeWriterThreshold`. A segment is
returned to the pool only after every active reader has consumed it.

A completed reader immediately leaves the active set and can unblock the writer
or permit reclamation. `FlushResult.IsCompleted` becomes true only after the
last reader completes. `CancelPendingRead` is per reader;
`CancelPendingFlush` is global.

### Completion and failures

`ReaderCompletions[index]` completes successfully with `null` for normal reader
completion or with the exception supplied to `Complete(exception)`. These tasks
never fault.

`TeePipeReaderFailureBehavior.Continue` is the default. A faulted reader leaves
the active set, the writer and healthy readers continue, and its exception is
only historical completion data.

With `CompletePipe`, the first concurrent reader fault becomes the pipe-wide
terminal exception. The writer faults and rejects further writes. Healthy
readers may drain already-flushed data and then observe the terminal exception;
TeePipe neither discards their buffers nor completes them on their behalf. Every
reader completion task still retains its own exception.

### Options

`TeePipeOptions` follows `PipeOptions` defaults:

- shared memory pool;
- thread-pool reader and writer schedulers;
- 64 KiB pause threshold;
- 32 KiB resume threshold;
- 4 KiB minimum segment size;
- synchronization-context capture enabled;
- reader-failure behavior `Continue`.

## Verification

- Unit tests cover API validation, consistency, exception aggregation,
  cancellation, ownership, buffered I/O and seeking, broadcast delivery,
  cursor independence, backpressure, completion, failure modes, and reset.
- Stress tests randomize independent reader progress and cancellation.
- An AOT smoke application is published in CI.
- Package contents and runtime dependency metadata are tested.
- BenchmarkDotNet experiments cover 4 KiB, 64 KiB, and 1 MiB payloads; curated
  results and conclusions remain in the repository.

## DynamicAllocationStream

`DynamicAllocationStream` exposes a sparse logical address space over a single
readable, seekable backing stream. Creation additionally requires an empty,
writable stream. The logical address range ends at `long.MaxValue - 1`, while
`Length` is the block-aligned end of the highest allocated, non-trimmed logical
block and may decrease after trim or compaction.

The creation block size is a power of two from 64 KiB through 256 MiB and
defaults to 1 MiB. A first write allocates and zero-initializes a physical block
unless it overwrites that whole block. Unallocated gaps and trimmed blocks read
as zero. `SetLength` is unsupported.

The block allocation table consists of raw little-endian 64-bit entries: zero
means unallocated and nonzero values are absolute, block-aligned physical
offsets. BAT and trim metadata are allocated in block-sized regions referenced
by a chained region table. The final physical region-table slot is reserved for
the chain link.

Payload and BAT data are written in place. Metadata transitions are protected
by two checksummed, generation-numbered roots and a bounded metadata-only redo
journal in header block zero. Flush establishes the wrapper's durability
boundary. A writable open replays an active valid journal to its home offsets;
a read-only open applies the same patches through an in-memory overlay.

Full-block trim immediately removes a block from logical liveness. Partial trim
zeroes only the requested bytes in place. Fast compaction releases trimmed
blocks and packs live payload and movable metadata toward the start; slow
compaction first performs those operations, then additionally identifies and
releases all-zero payload blocks. `EstimateCompactionSavings` performs only
allocation arithmetic and never scans payload for zeroes.

The exact byte layout, checksum coverage, commit protocol, recovery validation,
allocation strategy, and compatibility policy are normative in
[the DynamicAllocationStream format specification](dynamic-allocation-stream-format.md).

## ErasureCodeStream

`ErasureCodeStream` is under development for a later 0.1 prerelease milestone
and is not yet a public API. The format constants, managed SIMD Reed-Solomon
codec, and checksummed A/B member-superblock serializer are implemented; the
member I/O coordinator, journal replay, and public state and maintenance APIs
remain in progress.

See [the ErasureCodeStream overview](erasure-code-stream.md) for the safety
model and current status. Its proposed version-1 media format, quorum behavior,
crash recovery, state model, maintenance controls, and verification
requirements are defined in
[the ErasureCodeStream format specification](erasure-code-stream-format.md).
