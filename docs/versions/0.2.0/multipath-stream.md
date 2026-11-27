# Multipath streams

Status: implemented for reliable ordered `Stream` paths. The version-1 wire
format remains experimental. Acknowledgements, retransmission, and UDP adapters
are not implemented.

`TeeForge.Networking.MultipathSenderStream` distributes a logical byte sequence
across application-supplied paths. `MultipathReceiverStream` reorders,
deduplicates, or reconstructs groups and exposes sequential reads. Both derive
from `Stream`, are non-seekable, and support only their named direction.
Compose two sender/receiver pairs for full duplex.

Paths can be `NamedQuicStream` instances or other reliable ordered streams.
The application opens and authenticates transports. The core does not open
sockets, resolve endpoints, authenticate peers, or implement Ethernet PRP or
IEC 62439-3. Paths sharing an underlying connection may share a failure domain;
path count alone does not establish independence.

## Working example

This .NET 10 example uses two in-process pipes as connected transports. Sending
and receiving run concurrently so bounded queues and transport backpressure can
make progress. In a network application, these operations run at their
respective endpoints.

```csharp
using System.IO.Pipelines;
using TeeForge.Networking;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
CancellationToken token = timeout.Token;
var options = new MultipathStreamOptions(
    mode: MultipathStreamMode.Raid1,
    framePayloadSize: 16 * 1024,
    pathAvailabilityTimeout: TimeSpan.FromSeconds(5));

await using var sender = new MultipathSenderStream(options);
await using var receiver = new MultipathReceiverStream(sender.SessionId, options);
for (int index = 0; index < 2; index++)
{
    var pipe = new Pipe();
    Task<Guid> joinSender = sender.AddPathAsync(pipe.Writer.AsStream(), token).AsTask();
    Task<Guid> joinReceiver = receiver.AddPathAsync(pipe.Reader.AsStream(), token).AsTask();
    await Task.WhenAll(joinSender, joinReceiver);
    // The receiver reads the same path ID that the sender generated.
}

byte[] payload = new byte[1024 * 1024];
Random.Shared.NextBytes(payload);
using var source = new MemoryStream(payload);
using var destination = new MemoryStream();
Task receive = receiver.CopyToAsync(destination, token);
Task send = SendAsync();
await Task.WhenAll(send, receive);
Console.WriteLine(payload.AsSpan().SequenceEqual(destination.ToArray()));

async Task SendAsync()
{
    await source.CopyToAsync(sender, token);
    await sender.CompleteAsync(token);
}
```

Run initializers concurrently at the two endpoints if their greetings require
bidirectional interaction. An initializer runs before the multipath hello.
The sender writes and flushes the hello; the receiver reads and validates it.

## Modes and local status

Write-call boundaries are not preserved. A group holds up to `FramePayloadSize`
bytes in RAID 1 or RAID 0, or `ErasureDataShardCount * FramePayloadSize` bytes in
erasure mode. Flush publishes a partial group. Erasure shards retain their
configured size and pad unused space; logical length removes padding on receipt.

| Mode | Publication threshold | Receiver behavior | Failure behavior |
| --- | --- | --- | --- |
| `Raid1` | One path write succeeds; other copies continue asynchronously | First valid copy, published in sequence; duplicates discarded | Remaining copies may preserve a group; one path provides no redundancy |
| `Raid0` | The assigned path write succeeds | Reorders successive groups distributed across paths | An assigned-path write failure faults the sender; missing groups cannot be reconstructed |
| `ErasureCode` | `k` shard writes succeed; other shards continue asynchronously | Any `k` valid shards reconstruct a group | Fewer than `k` successful shard writes faults the sender; reconstruction requires receipt of `k` shards |

Erasure mode requires at least `k + r` locally active paths, where `k` and `r`
are the data and parity counts. Each shard uses a distinct path. With fewer
paths, subsequent groups use RAID 1 while desired mode remains erasure coding.
Adding enough paths restores erasure coding. Existing groups retain their
original metadata; new paths do not replay old groups.

`sender.Status` captures desired/effective mode, path count, shard counts,
membership epoch, lifecycle state, and protection under one lock. Individual
properties remain available, but separate reads may observe different moments.

| `Status.Protection` | Local meaning |
| --- | --- |
| `Unavailable` | No paths, or the sender is completed, faulted, or disposed |
| `Unprotected` | One mirrored path, or RAID 0 with any path count |
| `Mirrored` | At least two paths available for mirrored groups |
| `ErasureProtected` | Enough paths for all configured data and parity shards |

Lifecycle states are `Open`, `Completing`, `Completed`, `Faulted`, and
`Disposed`. Protection describes capacity for further publication, including
pending data during completion. It does not confirm redundant delivery of a
particular group. Underlying write success, path count, and path observations
are not remote delivery acknowledgements.

## Operation contract

| Operation | Contract |
| --- | --- |
| Sender `WriteAsync` / `Write` | Buffers partial groups and publishes full groups. Success can leave buffered bytes and redundant path writes running. |
| Sender `FlushAsync` / `Flush` | Publishes buffered bytes, then waits for all current path flushes in RAID 0 or at least one successful path flush in other modes. It does not confirm receiver consumption. |
| `ChangeModeAsync` | Publishes the previous partial group with the previous configuration, changes desired mode, and advances the epoch. The sender is authoritative. |
| `CompleteAsync` | Publishes pending data, mirrors an EOF marker naming the next sequence, and waits for at least one successful path flush. Concurrent calls serialize; repeated successful calls are harmless. Join paths before completion, which freezes further additions. |
| Receiver `ReadAsync` / `Read` | Returns ordered bytes. A nonempty read returns zero only after logical EOF and all preceding groups have been consumed. An empty read returns zero immediately. |
| Receiver `FlushAsync` / `Flush` | No-op while undisposed. |
| Sender `RemovePathAsync` | Removes a path at a publication boundary and sends retirement after queued writes. It has no cancellation parameter and can wait on a stalled path. |
| Receiver `RemovePathAsync` | Locally stops a pump without asking the sender to retire it. Queued frames remain available; unread or staged bytes on the detached path may be lost. |
| Disposal | Aborts the endpoint. Sender disposal without completion may discard buffered bytes. Receiver disposal wakes pending logical reads and releases queued/group buffers. |

Sender writes, flushes, mode changes, completion, and graceful removal serialize
with publication. Receiver reads serialize with each other. Paths can be added
while an ordinary data operation waits for connectivity. Synchronous calls
block on the asynchronous implementation; prefer async calls for network paths.

### Cancellation, outages, and errors

Cancelled or timed-out receiver reads leave frames available for the next read.
The receiver availability timeout runs only while no paths exist: joining an
idle path ends it, and losing the last path starts it. It is not a deadline for
a stalled transport or an incomplete group. Use a cancellation token for an
overall deadline. Synchronous calls use no cancellation token.

Sender operations without paths wait subject to cancellation and
`PathAvailabilityTimeout`. An outage timeout is an `IOException` with a
`TimeoutException` inner exception. The default is infinite. An unrecoverable
gap can therefore wait indefinitely unless the application sets a timeout or
cancels. New paths do not replay missing groups.

Cancelling a sender operation can leave a prefix buffered or published. There
is no rollback or accepted-byte count. Cancellation during group publication
faults the sender and removes interrupted write paths because a transport may
contain a partial frame. Start a new session after that fault. Cancellation
while merely waiting for an operation gate publishes no data. Do not blindly
resend an entire buffer after any interrupted write. Failure after completion
enters its publication phase faults the sender because EOF may be in flight.

Malformed frames fail their path; other valid copies or shards can still supply
the group. Inconsistent group metadata, conflicting duplicate payloads, invalid
completion sequences, and exceeded reorder limits fault the receiver. The
current RAID-0 receiver policy is conservative: after observing any RAID-0
data, an unexpected path failure processed before logical EOF faults the
session, even after a later mode change. In-band retirement avoids that policy.
Transport EOF alone is not logical EOF.

### Ownership

Ownership transfers only after `AddPathAsync` succeeds. After a failed or
cancelled initializer/hello, the caller retains responsibility for a stream
whose framing position may have changed. Do not retry a partial hello without
an application-defined reset. A receiver can bind to its first path's session
or require an expected session ID. Session IDs are not authentication.

After addition, the endpoint exclusively uses the supplied stream's bytes.
With `LeaveOpen = false`, removal, detected failure, and disposal close owned
paths; receiver pumps also close them at completion or retirement. With
`LeaveOpen = true`, streams stay open, but cancellation cannot immediately stop
a transport that ignores tokens. Coordinate transport shutdown before reusing
those bytes. Receiver-local detach is not graceful sender-side retirement.

## Options and resource bounds

`MultipathStreamOptions` remains shared for convenient paired construction.
Sender settings do not constrain received metadata. Receiver limits are
explicit and need not match the sender's initial mode.

| Setting | Endpoint | Default and range |
| --- | --- | --- |
| `Mode` | Sender | `Raid1`; a defined mode |
| `FramePayloadSize` | Sender | 16 KiB; 1 byte through 1 MiB per frame/shard |
| `ErasureDataShardCount` / `ErasureParityShardCount` | Sender | 4 / 2; data >= 2, parity >= 1, total <= 255 |
| `PathQueueCapacity` | Sender | 8 positive slots per path, including its active write |
| `MaximumReorderGroups` | Receiver | 1024; sequence distance must be strictly less than this positive value |
| `ReceiveQueueCapacity` | Receiver | 64 positive event slots shared by pumps |
| `MaximumReceiveFramePayloadSize` | Receiver | 1 MiB; 1 byte through 1 MiB; checked before allocating a frame body |
| `MaximumReceiveShardCount` | Receiver | 255; 1 through 255 total shards |
| `MaximumReorderBytes` | Receiver | 64 MiB; positive reservation for retained groups |
| `PathAvailabilityTimeout` | Both | Infinite when omitted; otherwise positive or `Timeout.InfiniteTimeSpan` |
| `LeaveOpen` | Both | `false` |

The bounded receiver queue waits when full and never drops frames or failure
events to make room. Each independent pump can stage one additional frame.
Application reads resume pumps and propagate backpressure through transports.

The reorder byte budget reserves one payload per RAID-1/RAID-0 group. An erasure
group reserves `(k + r) * shardSize + logicalLength`, including missing-shard
reconstruction and decoded output, before retaining the group. Consumption
releases the reservation. Exceeding the budget faults the receiver instead of
waiting for space that an earlier missing group might itself need.

This is not a process-wide heap limit. Queue payloads occupy at most
`ReceiveQueueCapacity * MaximumReceiveFramePayloadSize`. Each attached path
additionally needs staging/parsing storage, temporarily up to two payload-sized
arrays plus a header. Group metadata, codec matrices, and transport buffers are
additional. The application controls the number of attached paths.

The sender evicts a path when its send queue has no free slot. It does not
silently drop the only RAID-0 copy. A slow redundant path can finish after an
operation returns or be evicted as later groups fill its queue.

## Optional control channel

`MultipathControlChannel` frames messages over a separate reliable stream,
readable, writable, or both. One send and one receive can run concurrently;
same-direction calls serialize. Clean EOF returns `null`. Its `leaveOpen`
argument controls ownership independently of data options. Finish or cancel
control operations before disposal. Cancellation midway through a control frame
does not reset framing; recreate the channel/transport.

Only these messages are implemented:

| Kind | Checked accessor | Meaning |
| --- | --- | --- |
| `PathReceivingValidFrames` | `GetPathReceivingValidFrames()` | Returns the ID of a path observed receiving valid frames |
| `ModeChangeRequest` | `GetModeChangeRequest()` | Returns typed mode and shard counts for authorization |
| `EndpointAdvertisement` | `GetEndpointAdvertisement()` | Returns typed UTF-8 scheme and opaque data |

Accessors throw `InvalidOperationException` for the wrong kind. Payload data is
exposed only through these checked accessors. `CreatePathReceivingValidFrames`
constructs the path observation message, whose wire value is zero.

The channel does not automatically change modes, connect endpoints, or
acknowledge delivery. The application authorizes a request and calls
`ChangeModeAsync`; no acceptance handshake is required by the data plane.
Non-erasure request shard counts are zero: omit those arguments when calling
`ChangeModeAsync` to preserve its configured erasure geometry. Authenticate and
authorize endpoint hints before connecting and calling `AddPathAsync`.

## Version-1 wire format

A four-byte big-endian body length excludes its own prefix. Every body begins
with four-byte magic, one-byte version, one-byte kind, and two zero reserved
bytes. Integers and GUIDs use big-endian order. Data magic is `0x54464D50`,
control magic is `0x54464D43`, and version is 1.

Data body offsets exclude the length prefix:

| Kind | Body bytes | Fields after the 8-byte common header |
| --- | --- | --- |
| Hello (1) | 40 | Session GUID at 8; path GUID at 24 |
| Data (2) | 60 + payload | Session GUID at 8; epoch u64 at 24; sequence u64 at 32; mode, shard index, data count, parity count at 40..43; logical length i32 at 44; payload length i32 at 48; payload XxHash64 u64 at 52; payload at 60 |
| Complete (3) | 32 | Session GUID at 8; exclusive final sequence u64 at 24 |
| Retire (4) | 32 | Session GUID at 8; retirement epoch u64 at 24 |

Modes encode as RAID 1 = 0, RAID 0 = 1, erasure = 2. Sequences start at zero.
Membership and mode changes advance the epoch. Old groups remain decodable
from their original metadata. XxHash64 covers payload bytes only and provides
no authentication. Data frames carry no write-call boundary.

Control kinds are observation = 0, mode request = 1, endpoint advertisement = 2.
After the common header, payloads are respectively: a 16-byte path GUID; four
bytes containing mode, data count, parity count, and zero; or a one-byte UTF-8
scheme length, scheme bytes, two-byte big-endian data length, and opaque data.
Scheme length is at most 255 bytes and endpoint data at most 65,535 bytes.

## Deferred work and verification

Acknowledgements, selective missing-group reports, retransmission, automatic
remote health tracking, mode acceptance/rejection messages, endpoint withdrawal,
and UDP adapters are proposals. Reliable UDP additionally requires replay
buffers, timers, flow/congestion control, and an explicit reliability contract.
Framing and erasure coding alone cannot provide reliable datagram delivery.

Tests cover the three modes, path churn, reconstruction, fallback/restoration,
mode boundaries, initializers, control round trips and checked payloads,
timeout/cancellation recovery, bounded queue backpressure, queued failures,
receive limits, memory reservation/release, disposal of pending reads, status
transitions, concurrent completion, and interrupted publication. Randomized
transport faults, partial-frame cancellation, and long-running multipath stress
remain necessary before declaring the wire format stable.
