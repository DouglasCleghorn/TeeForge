# ReplicaStream

`ReplicaStream` turns one forward-only sequence of writes into the same sequence
on one or more writable streams. It is the smallest TeeForge mirroring surface:
there is no primary replica, no read comparison, no position agreement, and no
random-access contract.

## Basic use

```csharp
using TeeForge.Mirroring;

await using Stream archive = File.Create("archive.bin");
await using Stream upload = await OpenUploadAsync();
await using var output = new ReplicaStream(archive, upload);

await producer.CopyToAsync(output);
await output.FlushAsync();
```

Construction requires at least one replica. Every replica must be non-null and
writable, and the same stream object cannot appear more than once. The enumerable
constructor snapshots its input, so later collection changes have no effect.

## Stream contract

`ReplicaStream` supports `Write`, `WriteByte`, `WriteAsync`, `Flush`,
`FlushAsync`, `WriteTimeout`, and disposal. It reports `CanRead == false` and
`CanSeek == false`; reads, seeks, length, position, and set-length throw
`NotSupportedException`. `CanWrite` and `CanTimeout` require every replica to
advertise the corresponding capability and become false after disposal. The
`WriteTimeout` getter also requires every timeout-capable replica to report the
same value; setting it attempts the update on every replica.

Writes are not buffered. Each caller buffer is offered directly to every
replica during that operation. Wrap `ReplicaStream` in `BufferedStream` when a
producer emits many small writes and the replicas benefit from coalescing:

```csharp
await using var replicas = new ReplicaStream(first, second, third);
await using var output = new BufferedStream(replicas, 64 * 1024);
await producer.CopyToAsync(output);
```

Disposing the outer buffer flushes it and then disposes `ReplicaStream`, which
owns its replicas by default.

## Dispatch and failures

Async writes, flushes, and disposals start an operation on every applicable
replica before awaiting the group. Synchronous operations use deterministic
replica-index order by default. For replicas whose synchronous implementations
can safely run on separate threads, opt into concurrent dispatch:

```csharp
var options = new ReplicaStreamOptions(
    synchronousMode: TeeStreamSynchronousMode.Concurrent,
    leaveOpen: true);
await using var output = new ReplicaStream(options, first, second);
```

Every replica is attempted even when an earlier one fails. A single failure is
re-thrown directly. Multiple failures are contained in an `AggregateException`
in replica-index order. Pre-canceled async operations invoke no replica; after
dispatch starts, every operation is awaited so all failures can be observed.

Replication is not a transaction. A replica may consume some or all of a write
before throwing, while another replica may complete it. After any write or
flush failure, the caller must decide whether the destination set can be
reconciled, retried from a known boundary, or discarded.

## Ownership

The wrapper owns every replica unless `ReplicaStreamOptions.LeaveOpen` is true.
Both sync and async disposal attempt every owned replica. Ownership begins only
after constructor validation succeeds.

Use `LeaveOpen` when the replicas have a longer lifetime than the fan-out view:

```csharp
await using (var output = new ReplicaStream(
    new ReplicaStreamOptions(leaveOpen: true),
    first,
    second))
{
    await producer.CopyToAsync(output);
}

// first and second remain open.
```

## Choosing the related APIs

- Use `ReplicaStream` when consumers only write a forward sequence and replicas
  do not need matching read or seek capabilities.
- Use `TeeStream` when one logical readable or seekable stream must mirror all
  operations and verify successful results or read contents.
- Use `TeeBufferedStream` when reads or seeks are needed and logical I/O should
  be coalesced once before mirrored fan-out.
- Use `TeePipe` when one pipeline writer feeds independent readers that consume
  at their own pace and require backpressure.
