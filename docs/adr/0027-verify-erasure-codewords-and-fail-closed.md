# Verify erasure codewords and fail closed when inconsistency is ambiguous

An erasure consistency check validates individual headers and integrity hashes,
then verifies each current block against the configured Reed-Solomon codeword.
When valid systematic data identifies disagreeing parity, those parity members
are marked corrupt. If unavailable data makes a codeword inconsistency
impossible to localize, the stream faults instead of reconstructing from an
ambiguous set.

This detects coherent but mathematically invalid shard contents that per-shard
checksums alone cannot reveal. It sacrifices availability in the ambiguous
case, where choosing a fragment set could return plausible but incorrect data,
in favor of the stream's fail-closed integrity guarantee.
