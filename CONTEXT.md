# TeeForge

TeeForge provides .NET I/O primitives that distribute one producer's data to multiple consumers.

## Language

**Handoff stream**:
A stable stream endpoint that serializes operations and accepts a caller-created
replacement stream assumed to have the same final destination. The outgoing
stream is flushed before the replacement becomes active, without requiring
callers of the stable endpoint to replace, close, or reconnect it.

**Handoff boundary**:
The point after an active stream operation completes and before a queued
operation begins at which a HandoffStream atomically installs a replacement
stream after flushing the outgoing stream.

**Migrating stream**:
A stable readable, writable, seekable endpoint that copies a logical byte
sequence from a source stream to a destination stream in bounded background
quanta while foreground I/O remains available.

**Migrated prefix**:
The contiguous logical range beginning at offset zero that a MigratingStream
has copied to its destination. Reads in this range use the destination while
reads beyond it use the source until migration completes.

**Migration quantum**:
One bounded source-read and destination-write phase. It completes atomically
with respect to serialized foreground operations; queued foreground operations
run before the next quantum begins.

**Migration handoff**:
A HandoffStream transition that installs a MigratingStream over its current
source before copying starts and installs the destination after successful
migration. Failure restores the source unless the destination had already
become authoritative before optional source cleanup failed.

**TeeStream**:
A stream that mirrors operations across an arbitrary set of destination streams and presents them as one stream. An operation is supported only when every destination supports it.

**TeeBufferedStream**:
A TeeForge adaptation of Microsoft's .NET 10 BufferedStream. Its lazy shared buffer and large-I/O bypass process the logical byte sequence once, then use TeeStream semantics to broadcast emitted operations to every destination; it does not allocate one buffer per destination.

**TeeBufferedStreamOptions**:
An immutable, unsealed child of TeeStreamOptions that adds the positive shared BufferSize used by TeeBufferedStream and TeeHashStream. Buffered sequence constructors take this complete options object rather than accepting buffer size separately; TeeBufferedStream alone retains a direct buffer-size convenience overload.

**Random-access stream**:
A stream with explicit-offset operations over its logical byte sequence. A random-access operation preserves Position and is safe to invoke concurrently, although an implementation may serialize execution and may use an upstream random-access capability when one is available.

**Mutual QUIC connection**:
An authenticated relationship whose two endpoints load private-key-bearing
identities from local certificate and PEM key files and pin each other's
certificate. It owns one QUIC connection and can carry multiple independent
application streams opened by either endpoint.

**Named QUIC stream**:
One bidirectional QUIC stream dynamically associated with an application name
for the lifetime of a mutual QUIC connection. The name appears only in the
stream-opening handshake; QUIC assigns the physical stream ID used by the
transport. At most one live stream pair has a given name on a connection.

**Named-stream collision**:
Two endpoints attempting to open the same named QUIC stream concurrently. The
client-initiated stream wins deterministically, the server-initiated attempt is
rejected, and an already active name rejects later attempts until its stream is
disposed.

**QUIC stream compression**:
Transparent compression selected by the opener for one named QUIC stream and
accepted only when permitted by the receiving connection. The opening handshake
is uncompressed; after negotiation, the selected algorithm applies to all
payload bytes in both directions using independent compression contexts.

**QUIC random-access service**:
A connection-level service that exposes a caller-owned ITeeRandomAccessStream
to its peer independently of named sequential streams. Each positional request
uses its own short-lived bidirectional QUIC stream so requests can progress and
fail independently.

**QUIC random-access compression threshold**:
The minimum uncompressed request or response payload size at which a negotiated
random-access compression algorithm is applied. Smaller positional payloads
remain uncompressed.

**QUIC random-access request**:
An explicit-offset read or write carried on its own bidirectional QUIC
stream, separate from every named sequential stream. A negotiated short service
handle identifies the caller-owned ITeeRandomAccessStream that services it.

**HTTP random-access stream**:
A read-only stream whose backing bytes are retrieved with HTTP byte-range requests. It remains a thin transport; caching and speculative prefetch belong to a read-ahead wrapper.

**HTTP representation snapshot**:
The remote representation identity and length captured when an HTTP random-access stream opens. Every range returned by that stream belongs to the same snapshot when the configured validation mode can establish one.

**HTTP representation change**:
A detected mismatch between an HTTP random-access stream's captured representation snapshot and a later response. Configured retries continue targeting the original snapshot; recovery after the source faults requires opening a new source and cache stack.

**HTTP slowdown window**:
A shared earliest time at which an HTTP random-access stream may issue another request after a server asks it to slow down. All ranges opened by the stream observe the same window.

**Range read source**:
A source that can open independent bounded streams over explicit ranges of its logical byte sequence. Opening a range does not observe or change the source stream's Position.

**Range stream**:
A read-only, forward-only stream over one requested logical byte range. It owns the lifetime of the underlying range operation, cannot outlive its range read source, and abandoning it before its end discards the unread remainder without affecting sibling ranges.

**Range resumption**:
Continuation of an interrupted range stream by requesting from its next undelivered logical offset under the same representation-validation policy. Successfully delivered bytes are not requested again.

**I/O phase**:
A set of independent upstream I/O operations that may be issued together and awaited as a group before a dependent phase begins. This exposes queue depth without promising how the upstream executes it; ordering and parity dependencies separate phases.

**Destination stream**:
A stream configured to receive the data written through a TeeStream.

**Primary stream**:
The first destination stream. Its data and return values become TeeStream's observable result when the configured consistency policy tolerates differences between destinations.

**Primary-sized read**:
A TeeStream read in which the primary stream determines the returned byte count and every other destination is advanced by that same number of bytes before their content is compared.

**Consistency policy**:
The rule that determines whether TeeStream rejects differences between destination results or accepts the primary stream's result.

**Strict consistency**:
The default TeeStream consistency policy, under which differing return values or read data cause the current operation to fail without preventing later operations.

**Faulted stream**:
A TeeStream that refuses further operations after discovering inconsistent destination results. Faulting is an opt-in consistency policy rather than the default.

**Use primary**:
A TeeStream consistency policy that accepts differences between destinations and exposes the primary stream's data or return value.

**TeePipe**:
A pipe with one writer and a fixed set of readers that broadcasts the same byte sequence to every reader. Each reader observes the complete sequence independently rather than competing with other readers for data.

**Active reader**:
A TeePipe reader that has not completed and therefore still participates in broadcast delivery and flow control.

**Reader completion**:
The normal or exceptional end of one TeePipe reader's participation in the broadcast. Reader completions are independently observable by their fixed reader indexes.

**TeeHashStream**:
A write-only buffered stream that mirrors writes to ordinary destinations while computing one or more explicitly selected hashes of the same observed byte sequence. Every constructor receives its algorithm or algorithm collection as the first parameter; TeeHashStream has no implicit default algorithm.

**Hash destination**:
An internal TeeHashStream destination that observes writes and computes one configured hash without retaining the written content.

**Hash-observed sequence**:
The ordered bytes accepted by a hash destination. Each successful delivery is an observation, so bytes delivered again by buffered retry behavior are included again.

**Hash completion**:
The point at which every hash destination has finalized and TeeHashResults publishes all immutable results together. Hash completion is independent of whether an ordinary destination later fails to dispose.

**TeeHashAlgorithm**:
A TeeForge enum naming MD5, SHA-1, SHA-2, SHA-3, CRC, and XXHash algorithms supported by TeeHashStream. One TeeHashAlgorithm-based call may mix cryptographic and non-cryptographic members. Each member's public API description identifies its family so callers can distinguish security-oriented hashes from non-cryptographic checksums and fast content hashes in IntelliSense and generated documentation; MD5 and SHA-1 additionally warn about their broken collision resistance.

**Hash algorithm adapter**:
The public conversion boundary between HashAlgorithmName and TeeHashAlgorithm. Standard cryptographic names convert in both directions. A non-cryptographic TeeHashAlgorithm has no HashAlgorithmName representation, so its try-conversion returns false; adapting names does not add non-cryptographic behavior to the original HashAlgorithmName-based TeeHashStream path.

**TeeHashResults**:
A stable, externally read-only results collection returned during TeeHashStream construction. It is empty while hashing is in progress. After every configured hash has been finalized, all results are published together and IsComplete becomes true.

**TeeHashResult**:
An immutable completed hash value containing its HashAlgorithmName and digest bytes, with hexadecimal and Base64 representations computed lazily. Results published by TeeHashResults appear only after collection completion and do not carry an individual completion flag.

**TeeHashResult<TAlgorithm>**:
An immutable completed hash value keyed by an enum algorithm identifier, with immutable digest bytes and lazy hexadecimal and Base64 representations. TeeHashResult<TeeHashAlgorithm> is the result shape used by TeeHashAlgorithm-based TeeHashStream calls, while the existing non-generic TeeHashResult preserves the HashAlgorithmName API.

**TeeHashResults<TAlgorithm>**:
A stable, externally read-only dictionary from enum algorithm identifiers to TeeHashResult<TAlgorithm> values. TeeHashResults<TeeHashAlgorithm> supports mixed cryptographic and non-cryptographic algorithms and uses the same empty-until-complete, atomic-publication contract as the existing non-generic TeeHashResults.
