# TeeForge 0.1 specification

Status: accepted for implementation on 2026-08-22.

## Package

- Package ID and assembly name: `TeeForge`.
- Version: `0.1.0`.
- Target framework: `net10.0` only.
- Namespace: `TeeForge` for every public type.
- License: MIT.
- Public concrete classes remain unsealed.
- The package has no runtime NuGet dependencies.
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
  cancellation, ownership, broadcast delivery, cursor independence,
  backpressure, completion, failure modes, and reset.
- Stress tests randomize independent reader progress and cancellation.
- An AOT smoke application is published in CI.
- Package contents and runtime dependency metadata are tested.
- BenchmarkDotNet experiments cover 4 KiB, 64 KiB, and 1 MiB payloads; curated
  results and conclusions remain in the repository.
