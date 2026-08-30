# MultipathStream design

Status: initial reliable-`Stream` implementation and version-1 protocol draft.

## Purpose

`MultipathStream` combines several independently supplied transports into one
ordered logical byte stream. It is inspired by Parallel Redundancy Protocol
(PRP), but it is not an IEC 62439-3 implementation: in addition to sending
duplicate data, it can stripe data or generate erasure shards.

The intended first transport is `NamedQuicStream`. The framing and scheduling
layer must not depend on QUIC, IP addressing, or certificate types. An
application may therefore use another reliable `Stream`, or implement a
packet-transport adapter for a datagram protocol such as UDP.

## Goals

- Preserve one ordered logical byte sequence across multiple data paths.
- Add and remove data paths while the logical stream is active.
- Change distribution mode at an unambiguous frame boundary.
- Support RAID-1-like mirroring, RAID-0-like striping, and systematic
  Reed-Solomon erasure coding with configurable data and parity shard counts.
- Keep a configured erasure mode as the desired mode, but automatically use
  mirrored delivery while too few healthy paths exist for that erasure
  configuration. Restore erasure coding at a later boundary when capacity
  returns.
- Optionally use a reverse control path for acknowledgements, path-health
  reports, mode requests, and endpoint advertisements.
- Allow application code to initialize and authenticate a newly connected
  transport before MultipathStream takes ownership of its bytes.

## Non-goals

- Compatibility with Ethernet PRP frame formats or RedBox devices.
- Opening sockets, resolving advertised endpoints, or choosing an application
  authentication policy inside the core multipath layer.
- Hiding the very different durability guarantees of a reliable byte stream
  and an unreliable datagram transport.
- Recovering RAID-0 data assigned to a path that fails before delivering it.

## Public shape

The data plane should initially be directional rather than pretending every
transport is full duplex:

- a sender accepts logical writes and emits framed data on active paths;
- a receiver consumes framed data paths, reorders and reconstructs frames, and
  exposes logical reads;
- an optional reverse control transport connects the receiver to the sender.

Two sender/receiver pairs can be composed when an application needs a
full-duplex logical connection. This model also admits QUIC unidirectional
streams and UDP without reserving an otherwise unused data direction.

The first implementation accepts reliable ordered `Stream` paths directly and
adds a length prefix and exact-read loop. Each receiver path has an independent
frame pump, so a stalled byte-stream path does not block parsing on every other
path. A future UDP adapter would preserve one protocol frame per datagram and
impose a maximum frame size.

The principal API is:

```csharp
public enum MultipathStreamMode
{
    Raid1,
    Raid0,
    ErasureCode,
}

public class MultipathStreamOptions
{
    public MultipathStreamMode Mode { get; }
    public int FramePayloadSize { get; }
    public int ErasureDataShardCount { get; }
    public int ErasureParityShardCount { get; }
    public bool LeaveOpen { get; }
}

var options = new MultipathStreamOptions(
    mode: MultipathStreamMode.ErasureCode,
    erasureDataShardCount: 4,
    erasureParityShardCount: 2);

await using var sender = new MultipathSenderStream(options);
await using var receiver = new MultipathReceiverStream(sender.SessionId, options);

Guid senderPathId = await sender.AddPathAsync(outboundStream);
Guid receiverPathId = await receiver.AddPathAsync(inboundStream);

await sender.WriteAsync(payload);
await sender.CompleteAsync();
await receiver.CopyToAsync(destination);
```

`AddPathAsync` also has an initializer overload. The initializer runs before
the sender writes, or the receiver reads, the multipath hello. `RemovePathAsync`
changes later groups without changing the interpretation of frames already in
flight. `ChangeModeAsync` flushes the current partial group before making the
sender's new desired mode effective. A sender calls `CompleteAsync` to flush its
last partial group and publish logical EOF; disposing it without completion is
an abort and may discard buffered bytes.

`MultipathControlChannel` independently frames `MultipathControlMessage`
instances on an optional reliable control `Stream`. Applications receive a
mode request or endpoint advertisement, apply their own authorization policy,
and then call `ChangeModeAsync` or connect and add the suggested path.

## Data-plane protocol

Every frame needs a versioned, fixed-size header containing at least:

- protocol magic and version;
- session identifier;
- membership epoch;
- logical group sequence number;
- mode;
- shard index, data-shard count, and parity-shard count;
- logical payload length, including final-group padding information;
- payload checksum.

Multibyte integers use network byte order. The checksum detects corruption and
misbehaving adapters; transport encryption and peer authentication remain the
transport or application's responsibility.

Logical `Write` call boundaries are not preserved. Writes fill protocol groups
of a configured size. `Flush` publishes a partial group so interactive traffic
does not wait indefinitely for a full group.

### RAID 1

The sender places the same group sequence and payload on every selected path.
The receiver publishes the first valid copy and discards later duplicates. A
path failure does not delay delivery once another copy is valid.

One healthy path is a valid but unprotected RAID-1 state. Zero healthy paths
apply backpressure or fail according to a still-to-be-selected outage policy.

### RAID 0

The sender assigns consecutive groups across selected paths. The receiver
reorders them by group sequence. There is no recovery if an assigned group is
lost; the logical stream must fault rather than silently skip bytes.

### Erasure code

One group contains `k` systematic data shards and `r` Reed-Solomon parity
shards. Each shard is sent on a distinct path. The receiver can publish the
group after any `k` valid shards arrive. TeeForge's existing internal
`ReedSolomonCodec` can encode and reconstruct the shard set.

The configured erasure mode requires at least `k + r` healthy paths. If fewer
are available, the sender starts a new membership epoch in RAID 1. The desired
mode remains erasure coding, so reaching `k + r` paths later starts another
epoch and restores it. A mode never changes within a group.

This fallback preserves the byte sequence but may provide only one copy when a
single path remains. State reporting must distinguish `Mirrored`,
`Unprotected`, and `Unavailable` rather than describing all three as healthy.

## Dynamic membership

Each path has a stable identifier within a session. Adding or gracefully
removing a path creates a new monotonically increasing membership epoch. The
new set becomes active only for the next complete group; frames from an older
epoch remain decodable under their original parameters.

A newly added path first exchanges a join record containing the session ID,
path ID, and protocol version. Its first data frame names the membership epoch
in which it became active. This prevents an unrelated or delayed transport
from being attached to the wrong logical stream.

A graceful sender-side removal writes an in-band retirement frame at a group
boundary before closing the path. The receiver therefore distinguishes an
expected retirement from an unexpected end of stream; the latter faults an
active RAID-0 session because the failed path may own an unpublished group.

Unexpected failure differs from graceful removal. A receiver can immediately
ignore a failed duplicate or missing erasure shard, but RAID 0 faults if the
failed path owned an unpublished group. A sender cannot know that bytes reached
the receiver merely because an underlying write completed.

## Optional reverse control plane

The reverse path carries length-delimited, versioned control messages. Proposed
messages include:

- cumulative receive acknowledgement and selective missing-group report;
- path accepted, healthy, degraded, or failed;
- request or suggest mode and erasure parameters;
- mode accepted or rejected, with the effective epoch;
- advertise or withdraw an endpoint;
- graceful session completion and fatal protocol error.

Data frames remain self-describing. Losing the optional control path must not
make already received groups undecodable.

Mode changes are negotiated rather than applied unilaterally: one peer
proposes parameters, the other accepts or rejects them, and an accepted change
names its first membership epoch. Endpoint advertisements are hints. The
application validates, authorizes, and connects them before passing a new link
to MultipathStream.

If the reverse path is absent, the receiver can still deduplicate RAID 1 and
reconstruct erasure groups, but the sender has no delivery acknowledgement,
remote health information, or remote endpoint discovery.

## Reliability contract

Reliable underlying streams can provide an ordered logical stream without
protocol retransmission, subject to the selected mode's failure behavior.
UDP cannot provide that same contract from framing and erasure coding alone:
packets may be lost even while every path remains nominally healthy.

There are two honest choices for UDP:

1. document it as best-effort and let a missing unrecoverable group fault the
   logical stream; or
2. make acknowledgements, retransmission windows, timers, congestion control,
   and bounded replay buffers a required reliability layer.

The second choice is substantial and overlaps protocols such as QUIC. The
initial implementation should use reliable `Stream` links unless reliable UDP
is an explicit requirement.

## Initialization and endpoint advertisement

The core should accept already connected links. A caller-supplied asynchronous
initializer may run before the join record so an application can perform a
transport-specific greeting or bind metadata to a connection. Once the join
record starts, the link belongs exclusively to MultipathStream.

Endpoint advertisements should use a small transport-neutral envelope with a
scheme and opaque payload, not `IPEndPoint`. This admits DNS names, IPv4/IPv6,
QUIC options, relays, Unix sockets, and application-defined transports. The
application decides whether to connect and converts the advertisement into an
already connected `Stream` path.

Advertisements must be authenticated by the control transport or carry an
application-verifiable signature. Automatically connecting to unauthenticated
advertisements would create a redirection and network-scanning primitive.

## Backpressure and resource limits

The receiver needs bounded reorder and duplicate windows. A fast path must not
allow an unlimited number of groups to accumulate while an earlier RAID-0
group is delayed. Erasure groups release shard buffers after publication.

The sender needs per-path queues so a slow mirrored path does not delay the
first successful copy, plus explicit queue limits and an eviction policy. The
default should remove a path that exceeds its queue budget from a later epoch;
it must not silently discard a frame that is the only RAID-0 copy.

Synchronous `Stream` methods can delegate to serialized asynchronous
operations, but only one read and one write should be allowed concurrently.
Membership and mode changes must serialize with group publication.

## Accepted decisions

- The public data plane is directional. Applications compose two pairs for a
  full-duplex logical connection.
- The first release accepts reliable ordered `Stream` paths. Raw UDP remains a
  future adapter with an explicitly selected reliability contract.
- Erasure mode falls back to RAID 1 whenever fewer than `k + r` paths are
  active, including the degenerate unprotected one-path state.
- The desired erasure mode remains set and restores automatically when `k + r`
  paths become active again.
- A new path begins at a later membership epoch; old logical groups are not
  replayed to it.
- The sender is authoritative for mode changes. A receiver can send a request
  through the optional control channel.
- A RAID-1 write completes after one underlying path accepts the frame. Other
  path writes continue through bounded per-path queues. A receiver health
  message is advisory rather than an implicit acknowledgement for that write.
- With no paths, data operations wait for a path subject to cancellation and
  `PathAvailabilityTimeout`.
- Endpoint advertisements contain a UTF-8 scheme and opaque binary application
  data. The receiving application authenticates and interprets them.

## Implementation and verification status

Implemented:

- versioned hello, data, completion, and control frames;
- bounded frame payloads, checksums, and receiver reorder windows;
- RAID 1 first-valid-copy delivery and duplicate suppression;
- RAID 0 group scheduling and ordered recombination;
- Reed-Solomon encoding and reconstruction through the existing codec;
- automatic RAID 1 fallback and erasure restoration;
- dynamic path addition and removal at membership epochs;
- sender-controlled mode changes at complete-group boundaries;
- reliable path, mode-request, and endpoint-advertisement control messages;
- path initializers and configurable ownership.

The first test set covers mirrored duplication, RAID 0 ordering and path churn,
reconstruction with two absent erasure paths, fallback and restoration, writes
waiting for their first path, and control-message round trips. Further stress
work should randomize cancellation, partial frames, corruption, slow paths,
queue eviction, and transitions at every group boundary before the wire format
is declared stable. UDP remains deferred until its delivery contract is chosen.
