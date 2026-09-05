# Use cache-aware virtual seeks in ReadAheadStream

Status: proposed design; ReadAheadStream is not implemented in the current package.


ReadAheadStream requires an underlying seekable stream but, when cache use is enabled and a seek target is already cached, updates only its logical position instead of physically aligning the underlying stream; other seeks continue to delegate. Every successful seek immediately retargets background read-ahead, trading strict delegation and immediate source error timing for fewer physical seeks and earlier warming of the caller's likely next range.
