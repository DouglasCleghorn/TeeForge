# TeeForge

TeeForge provides .NET I/O primitives that distribute one producer's data to multiple consumers.

## Language

**TeeStream**:
A stream that mirrors operations across an arbitrary set of destination streams and presents them as one stream. An operation is supported only when every destination supports it.

**TeeBufferedStream**:
A TeeForge adaptation of Microsoft's .NET 10 BufferedStream. Its lazy shared buffer and large-I/O bypass process the logical byte sequence once, then use TeeStream semantics to broadcast emitted operations to every destination; it does not allocate one buffer per destination.

**TeeBufferedStreamOptions**:
An immutable, unsealed child of TeeStreamOptions that adds the positive shared BufferSize used by TeeBufferedStream and TeeHashStream. Buffered sequence constructors take this complete options object rather than accepting buffer size separately; TeeBufferedStream alone retains a direct buffer-size convenience overload.

**ReadAheadStream**:
A read/write stream wrapper that anticipates future reads by fetching data beyond the current request into a multi-range read cache. Disabling read-ahead stops future speculative fetching without discarding cached data.

**Random-access stream**:
A stream with explicit-offset operations over its logical byte sequence. A random-access operation preserves Position and is safe to invoke concurrently, although an implementation may serialize execution and may use an upstream random-access capability when one is available.

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

**In-flight range**:
A cache range with one active producer and zero or more waiting readers. Overlapping reads join the existing fill rather than opening a duplicate upstream range.

**Range reservation**:
The complete planned length charged to the cache budget before an in-flight range opens. Resident bytes plus reserved-but-not-yet-resident bytes do not exceed the cache budget.

**Progressive cache fill**:
A cache fill that advances a contiguous available prefix for foreground use while the range stream supplying later bytes is still being consumed. Readers may complete before an internal cache segment or the complete range has arrived.

**Adaptive read-ahead window**:
The forward range length selected for the next speculative fill. It grows with sustained sequential consumption, resets after discontinuous access, and is bounded by configured limits, remaining cache capacity, and end of stream.

**I/O phase**:
A set of independent upstream I/O operations that may be issued together and awaited as a group before a dependent phase begins. This exposes queue depth without promising how the upstream executes it; journal publication, parity dependencies, and durability barriers separate phases.

**Cache bypass**:
A ReadAheadStream mode in which foreground reads do not use cached data but reads, writes, and background read-ahead continue updating the cache. A non-seekable stream must still consume unread prefetched data to preserve the byte sequence.

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

**TeeHashPipe**:
A TeePipe that also computes hashes of the writer's broadcast byte sequence. Each configured hash is computed once regardless of the number of public readers.

**Hash reader**:
A TeeHashPipe participant that computes one configured hash algorithm over the broadcast byte sequence. It has the same cursor, lifetime, and backpressure semantics as every other active reader.

**TeeHashResults**:
A stable, externally read-only results collection returned during TeeHashStream or TeeHashPipe construction. It is empty while hashing is in progress. After every configured hash has been finalized, all results are published together and IsComplete becomes true.

**TeeHashResult**:
An immutable completed hash value containing its HashAlgorithmName and digest bytes, with hexadecimal and Base64 representations computed lazily. Results published by TeeHashResults appear only after collection completion and do not carry an individual completion flag.

**TeeHashResult<TAlgorithm>**:
An immutable completed hash value keyed by an enum algorithm identifier, with immutable digest bytes and lazy hexadecimal and Base64 representations. TeeHashResult<TeeHashAlgorithm> is the result shape used by TeeHashAlgorithm-based TeeHashStream calls, while the existing non-generic TeeHashResult preserves the HashAlgorithmName API.

**TeeHashResults<TAlgorithm>**:
A stable, externally read-only dictionary from enum algorithm identifiers to TeeHashResult<TAlgorithm> values. TeeHashResults<TeeHashAlgorithm> supports mixed cryptographic and non-cryptographic algorithms and uses the same empty-until-complete, atomic-publication contract as the existing non-generic TeeHashResults.

**Dynamic allocation stream**:
A logical stream with a maximum addressable capacity of `long.MaxValue` whose bytes are stored in fixed-size physical blocks allocated on first write. Its length is the block-aligned end of the highest logical block that still represents live data, capped at `long.MaxValue`. Reading an unallocated block below that length returns zeroes without allocating storage.

**Dynamic logical length**:
The cached exclusive block-end offset of the highest logical block that still represents live data, capped at `long.MaxValue`. Reads at or beyond it return end-of-stream; unwritten bytes and sparse gaps below it return zeroes. Trim and zero-block reclamation can reduce it, and recovery can recompute it from replayed allocation and trim metadata.

**Block allocation table (BAT)**:
A persistent array of 64-bit entries that maps logical block indexes to absolute, block-aligned byte offsets in the underlying stream. A zero entry means that the logical block is unallocated. BAT storage is allocated lazily in block-sized regions and existing BAT entries are updated in place.

**Trim table**:
Persistent block-granular metadata that records fully trimmed logical blocks. A trim-marked block immediately stops counting as live, reads as zero until rewritten, and may lower logical length when it was at the tail. Its previous physical block remains BAT-addressed until fast compaction reclaims it. Unaligned trim boundaries are zeroed by overwriting their allocated boundary blocks in place.

**Region table**:
Metadata composed of fixed 32-byte entries that locate BAT and trim regions by logical region index and absolute, block-aligned physical offset. The final physical slot is reserved for a chained sub-region table. The primary table resides in the header block and is indexed in memory when opened.

**Metadata root**:
One of two generation-numbered, checksummed headers describing the current format and journal state. Opening selects the newest valid root and replays its active metadata journal before allowing ordinary I/O.

**Committed generation**:
The format state selected by the newest valid metadata root. It does not by itself version payload bytes or BAT entries overwritten in place.

**Metadata journal**:
A header-resident circular redo log of independently checksummed 4 KiB entries that makes in-place BAT, trim-table, and region-table updates recoverable. Each record replaces one aligned 64-bit metadata word. Journal records are made durable before their described metadata writes reach their home locations; payload overwrites are not journaled.

**Read-only recovery**:
Opening a dynamic allocation stream without write access by replaying its active metadata journal into a volatile word-patch overlay. Metadata reads consult the overlay, leaving the underlying journal unchanged and disabling all mutating operations.

**Read-only mode**:
A dynamic allocation stream mode selected explicitly or forced by a non-writable backing stream. It permits logical reads and compaction-savings estimation while disabling writes, trim, compaction, and background free-space discovery.

**Partial first write**:
A write that covers only part of an unallocated or fully trimmed logical block. The complete physical block is initialized to zero before the caller's bytes become visible, preventing discarded or previously stored bytes from appearing in the remainder.

**Differencing stream**:
A logical stream that overlays private changes from a difference stream onto a base stream. Unchanged logical ranges read from the base, and another differencing stream may serve as the base to form a chain.

**Base stream**:
The logical byte sequence inherited by a differencing stream. A persistent base identifier binds the difference stream to its intended base.

**Difference stream**:
The persistent child storage containing a differencing stream's private changes and the metadata that locates them.

**Differencing logical length**:
The block-aligned end of the highest logical block that is live in either the difference stream or its inherited base, capped at `long.MaxValue`. An erased child block masks the corresponding base block and does not count as live.

**Erased block**:
A differencing-stream block state that represents logical zeroes and prevents reads from falling through to the base stream. It is distinct from an absent child block, which inherits its contents from the base.

**ErasureCodeStream**:
A readable, writable, seekable logical stream whose data is distributed across an erasure set using systematic Reed-Solomon coding. It can reconstruct unavailable member data while enough members remain.

**Erasure set**:
The complete collection of member streams that jointly stores one ErasureCodeStream.

**Member stream**:
A readable, writable, seekable underlying stream that occupies one persistent position in an erasure set and stores either data or parity shards.

**Data shard**:
One systematic Reed-Solomon portion of a stripe that contains logical stream bytes directly.

**Parity shard**:
One Reed-Solomon portion of a stripe computed from its data shards and used to reconstruct unavailable shards.

**Shard size**:
The configured, power-of-two number of payload bytes stored by each member for one stripe. It is persisted as part of the erasure-set format.

**Logical capacity**:
The fixed seekable length exposed by one erasure-set configuration. It changes only when a reshape publishes a new configuration.

**Reshape**:
A maintenance operation that migrates an erasure set to a new data-member or parity-member count, thereby changing capacity or resiliency.

**Stripe journal**:
A bounded persistent recovery area that makes interrupted in-place stripe updates replayable without retaining general write history.

**Stripe generation**:
The identifier shared by the data and parity shards produced by one committed update of a stripe. A member whose shard carries another generation is stale for that stripe.

**Integrity block**:
The independently checksummed, read-modify-write portion of a shard. Partial writes are expanded to integrity-block boundaries before parity and checksums are updated.
