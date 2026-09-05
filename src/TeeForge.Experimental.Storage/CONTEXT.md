# Experimental storage vocabulary

These terms describe research APIs and formats, not supported TeeForge stream helpers.

**Dynamic allocation stream**:
A logical stream with a positive, immutable, 4 KiB-aligned virtual capacity whose bytes are stored in fixed-size physical blocks allocated on first write. Its length is the block-aligned end of the highest logical block that still represents live data, capped at the virtual capacity. Reading an unallocated block below that length returns zeroes without allocating storage.

**Dynamic logical length**:
The cached exclusive block-end offset of the highest logical block that still represents live data, capped at `VirtualCapacity`. Reads at or beyond it return end-of-stream; unwritten bytes and sparse gaps below it return zeroes. Trim and zero-block reclamation can reduce it, and recovery can recompute it from replayed allocation and trim metadata.

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
The block-aligned end of the highest logical block that is live in either the difference stream or its inherited base, capped by immutable virtual capacity. An erased child block masks the corresponding base block and does not count as live.

**Erased block**:
A differencing-stream block state that represents logical zeroes and prevents reads from falling through to the base stream. It is distinct from an absent child block, which inherits its contents from the base.

**Presence grain**:
The fixed 4 KiB logical unit for which a partially present differencing block selects either child data or inherited base data. Presence grains and every physical structure containing them begin at 4 KiB-aligned offsets.

**Differencing block state**:
The BAT-encoded source of one logical block: inherited blocks read from the base, erased blocks read as zeroes, fully present blocks read from the difference stream, and partially present blocks select the difference stream or base independently for each presence grain.

**Differencing trim**:
An absolute logical range discard that deterministically reads as zero and never exposes inherited base data. It does not extend logical length or change Position, and trimming live tail blocks may reduce logical length.

**Data write identifier**:
A persistent identifier for one version of a stream's caller-visible logical byte sequence. It changes before the first logical mutation of a writable open but not when compaction changes only physical layout, allowing a differencing stream to reject a modified base.

**Differencing state record**:
An immutable, checksummed, allocation-block-sized child metadata record containing one logical block's VHDX-numbered BAT value and 4 KiB presence bitmap. Redundant roots select an append-only record tail, making payload-plus-record publication recoverable without the dynamic format's in-place metadata journal.

**Dependent stream registration**:
Advisory upstream metadata recording the stable identifier of a known immediate differencing child. A differencing-stream creation option may request registration, but ordinary differencing I/O never mutates the base; registrations may become stale, do not prevent upstream writes, and do not change the base's caller-visible logical data identity.

**Base identity validation**:
The creation- or open-time comparison of a differencing stream's recorded base identifier and data write identifier with the supplied base. Validation is not continuously repeated; mutating the base during the child wrapper's lifetime violates its ownership contract.

**Stream identity**:
The stable stream identifier and current data write identifier exposed together by an identity-capable stream. TeeForge block streams provide this identity directly, while callers supply it for other base-stream types.

**TeeForge disk image**:
A mountable sparse virtual-disk image stored with the `.tfdisk` extension and interpreted by `SparseDiskImage`. Its caller-visible stream length remains allocation-derived while its persisted virtual capacity supplies the stable disk size presented to an operating system.

**TeeForge difference image**:
A mountable differencing virtual-disk image stored with the `.tfdiff` extension and interpreted by `DifferencingDiskImage`. A writable leaf may inherit through a read-only chain of TeeForge disk or difference images with matching identity and geometry.

**Virtual capacity**:
The positive, immutable, persisted, 4 KiB-aligned disk size advertised by a mount host. It is distinct from allocation-derived stream length, is shared by every member of a differencing chain, bounds reads, writes, and trims, and is required when a TeeForge disk image is created.

**Parent locator hint**:
An optional relative path stored by a difference image to help a mount tool find its immediate base. The hint is not identity; a located candidate must still match the recorded base identifier and data write identifier.

**Mount broker**:
A separate user-mode host that translates Windows block-device requests into TeeForge random-access, trim, and flush operations. Version-one mounting runs one broker process per session-scoped data-disk mount; the broker contains no disk-format implementation, so all sparse allocation, differencing, chaining, journaling, and recovery remain in the TeeForge library.

**Deferred Storport driver**:
The intended signed Windows 11 virtual-storage miniport that will expose TeeForge images as native SCSI direct-access disks while delegating every format and logical-I/O operation to the user-mode mount broker. Implementation is deferred; the current ImDisk transport is a non-shipping prototype, and the future driver must remain format-agnostic.

**Session-scoped data-disk mount**:
A Windows mount that lasts only for the current operating-system session and is intended for ordinary data volumes. It is not automatically restored after reboot and is unsupported for boot, system, pagefile, hibernation, or crash-dump storage.

**ErasureCodedVolume**:
A readable, writable, seekable logical stream whose data is distributed across an erasure set using systematic Reed-Solomon coding. It can reconstruct unavailable member data while enough members remain.

**Erasure set**:
The complete collection of member streams that jointly stores one ErasureCodedVolume.

**Member stream**:
A readable, writable, seekable underlying stream with one stable identity and member slot in an erasure set. Its codeword position can differ by extent and stripe.

**Member slot**:
The stable set-relative index of one physical member identity. A member slot does not imply a permanent data or parity role.

**Declared member capacity**:
The caller-supplied and persisted maximum physical length that an erasure member may use. The allocator never infers additional usable capacity from filesystem free space or an underlying stream's ability to grow.

**Codeword position**:
One systematic-data or parity row in an extent's Reed-Solomon codeword before the extent's placement mapping assigns that row to a physical member slot.

**Distributed parity**:
A persisted deterministic placement mapping that rotates codeword positions across physical member slots by stripe, distributing systematic-data and parity traffic without changing the Reed-Solomon codeword.

**Data shard**:
One systematic Reed-Solomon portion of a stripe that contains logical stream bytes directly.

**Parity shard**:
One Reed-Solomon portion of a stripe computed from its data shards and used to reconstruct unavailable shards.

**Shard size**:
The configured, power-of-two number of payload bytes stored by each member for one stripe. It is persisted as part of the erasure-set format.

**Logical capacity**:
The seekable length exposed by the currently published erasure-set configuration. It increases atomically when capacity expansion publishes another allocation extent and may otherwise change only through an explicit migration or reshape.

**Set encoding parameters**:
The codec family, finite-field convention, shard size, and integrity-block size shared by every allocation extent in one erasure set. Changing them requires set migration.

**Extent geometry**:
An allocation extent's data- and parity-shard counts, member/codeword placement mapping, stripe count, logical range, and physical member regions under the set's immutable encoding parameters.

**Allocation extent**:
A contiguous logical range whose physical member regions, member mapping, and erasure-code geometry remain fixed after publication. One ErasureCodedVolume may append allocation extents with different geometries so capacity can grow without rewriting earlier extents.

**Capacity expansion**:
An asynchronous maintenance operation that reserves and publishes another allocation extent. Publication atomically increases logical capacity; existing extents and their stored payloads do not change.

**Extent publication**:
The durable configuration-generation commit that makes a prepared allocation extent authoritative, exposes its logical range as initialized zeroes, and atomically increases ErasureCodedVolume length.

**Stripe activation map**:
A replicated persistent bitmap with one bit per logical stripe in an allocation extent. An unset bit makes the stripe authoritative zeroes without consulting its physical shard regions; the first committed write publishes complete member headers before setting the bit.

**Set migration**:
A maintenance operation that copies an erasure set's logical byte sequence into a separately formatted, disjoint target set. The source remains authoritative until the complete target has been validated and selected by the caller.

**Reshape**:
A maintenance operation that re-encodes an existing logical range under a different extent geometry. It is distinct from append-only capacity expansion and from copying into a disjoint target set.

**In-place reshape**:
A reshape that progressively converts overlapping member storage from a source geometry to a target geometry without retaining a complete second copy of the set.

**Reshape intent**:
A persistent maintenance record that names the source and target geometries, conversion direction, aligned conversion quantum, and committed conversion frontier for one resumable in-place reshape.

**Conversion frontier**:
The durable logical boundary between ranges already governed by an in-place reshape's target geometry and ranges still governed by its source geometry.

**Conversion quantum**:
The smallest logical range whose boundaries align with complete stripes in both the source and target geometries. An in-place reshape durably journals and converts one such range before advancing its conversion frontier.

**Reshape plan**:
A non-mutating feasibility result that reports an in-place reshape's conversion direction and quantum, read and write estimates, per-member capacity and journal requirements, and whether its overlapping write order is provably safe.

**Paused maintenance intent**:
A persistent mutating maintenance intent stopped at a durable operation boundary by cancellation, disposal, or a recoverable member loss. Opening finishes any already journaled quantum, keeps the intent paused, and requires an explicit resume rather than rollback.

**Heal**:
A maintenance operation that rewrites damaged or stale shard ranges to the same available member stream at its existing persistent position.

**Rebuild**:
A maintenance operation that populates a newly supplied member stream for one persistent position from the surviving current shards.

**Member replacement**:
The complete operation that introduces a new member identity at an existing persistent position and rebuilds its shard contents.

**Replacement intent**:
A persistent maintenance record naming the source and target configurations, persistent position, and new member identity for one resumable member replacement.

**Provisional member**:
A replacement member named by a replacement intent that participates in foreground updates only for stripes whose current-generation shard it has already published. It becomes an ordinary member when the target stable configuration is published.

_Avoid: repair, when heal, rebuild, member replacement, or reshape is intended._

**Stripe journal**:
A bounded persistent recovery area that makes interrupted in-place stripe updates replayable without retaining general write history.

**Stripe generation**:
The identifier shared by the data and parity shards produced by one committed update of a stripe. A member whose shard carries another generation is stale for that stripe.

**Integrity block**:
The independently checksummed, read-modify-write portion of a shard. Partial writes are expanded to integrity-block boundaries before parity and checksums are updated.

**Codeword inconsistency**:
A stripe-generation block whose individually valid shards do not satisfy the configured Reed-Solomon relationship. It is localized when valid systematic data identifies disagreeing parity members; otherwise its source is unknown.

**Consistency finding**:
One consistency-check result identifying a stripe and integrity-block offset together with the implicated member positions, or recording that a codeword inconsistency could not be localized.

**Consistency check**:
A non-mutating maintenance operation that validates current member metadata, integrity blocks, and Reed-Solomon codewords. It may update volatile member condition and report bounded findings, but it never rewrites stored shards.
