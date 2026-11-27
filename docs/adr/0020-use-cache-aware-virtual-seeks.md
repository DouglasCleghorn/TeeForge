# Use cache-aware virtual seeks in ReadAheadStream

The proposal below covers a writable, adaptive background cache. TeeForge 0.2
implements a read-only window over independent ranges. Its foreground reads
and seeks are serialized. See the [current specification](../specification.md).

ReadAheadStream requires an underlying seekable stream but, when cache use is enabled and a seek target is already cached, updates only its logical position instead of physically aligning the underlying stream; other seeks continue to delegate. Every successful seek immediately retargets background read-ahead, trading strict delegation and immediate source error timing for fewer physical seeks and earlier warming of the caller's likely next range.
