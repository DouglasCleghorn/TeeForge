# MultipathStream design

Status: exploratory design; names and public API are not yet committed.

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

## Proposed shape

The data plane should initially be directional rather than pretending every
transport is full duplex:

- a sender accepts logical writes and emits framed data on active paths;
- a receiver consumes framed data paths, reorders and reconstructs frames, and
  exposes logical reads;
- an optional reverse control transport connects the receiver to the sender.

Two sender/receiver pairs can be composed when an application needs a
full-duplex logical connection. This model also admits QUIC unidirectional
streams and UDP without reserving an otherwise unused data direction.

The core should operate on message-preserving data links. A reliable `Stream`
adapter adds a length prefix and exact-read loop. A UDP adapter preserves one
protocol frame per datagram and must impose a maximum frame size. Keeping the
message boundary below MultipathStream prevents a stalled byte-stream path
from blocking parsing on every other path.

An early API sketch is:

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

public interface IMultipathDataLink : IAsyncDisposable
{
    ValueTask SendAsync(
        ReadOnlyMemory<byte> frame,
        CancellationToken cancellationToken = default);

    ValueTask<int> ReceiveAsync(
        Memory<byte> frame,
        CancellationToken cancellationToken = default);
}
```

The exact public split between sender, receiver, session, and link adapter is
an open decision. The interface above illustrates the required packet
boundary; it is not yet a committed API.

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
path ID, protocol version, and next usable epoch. This prevents an unrelated
or delayed transport from being attached to the wrong logical stream.

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
`IMultipathDataLink`.

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

## Decisions required before implementation

1. Is the public abstraction directional, as proposed, or must one
   `MultipathStream` be full duplex?
2. Is best-effort UDP acceptable, or is reliable delivery over raw UDP a core
   requirement?
3. Does "not enough streams for erasure" mean fewer than `k + r` paths, as
   proposed, or only fewer than the `k` paths needed to decode?
4. Should erasure mode automatically return when `k + r` paths are healthy,
   or require an explicit/negotiated change?
5. In RAID 1, does a logical write succeed after one path accepts it, after all
   current paths accept it, or only after an optional receiver acknowledgement?
6. When no paths are healthy, should writes wait for a path, fail immediately,
   or buffer up to a configured limit and timeout?
7. Is the reverse control transport one reliable ordered `Stream`, or must it
   also tolerate loss and reordering?
8. May either peer propose modes and endpoints, or is one side authoritative?
9. Are advertised endpoints represented as URI-like text, an opaque binary
   application payload, or a set of built-in endpoint records?
10. Must a newly joined path receive data already in flight, or may it start at
    the next membership epoch as proposed?
11. Should the first release expose only reliable `Stream` adapters and leave
    the message-link interface internal until a UDP design is proven?
12. What name best describes the feature: `MultipathStream`,
    `RedundantStream`, or `ParallelRedundancyStream`?

## Suggested implementation sequence

1. Freeze directional semantics, reliability guarantees, and mode-transition
   authority.
2. Specify golden binary vectors for join records and data-frame headers.
3. Implement an internal reliable-`Stream` link adapter and bounded frame
   parser.
4. Implement fixed-membership RAID 1 with duplicate suppression.
5. Add membership epochs and graceful add/remove.
6. Add RAID 0 ordering and explicit loss faults.
7. Reuse `ReedSolomonCodec` for erasure groups and automatic RAID 1 fallback.
8. Add the optional control codec, negotiation, health, and advertisements.
9. Stress cancellation, partial frames, path churn, corruption, slow paths,
   and transitions at every frame boundary.
10. Consider a UDP adapter only after its reliability contract is selected.
