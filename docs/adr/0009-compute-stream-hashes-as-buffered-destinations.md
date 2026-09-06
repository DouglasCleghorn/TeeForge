# Compute TeeHashStream hashes as buffered destinations

`TeeHashStream` will derive from `TeeBufferedStream` and add one internal
`HashWriteStream` destination for every configured algorithm. Cryptographic
destinations use `IncrementalHash`; non-cryptographic destinations use
Microsoft's `NonCryptographicHashAlgorithm` implementations. It is consequently
write-only: the write-only hash destinations participate in TeeStream's
capability intersection, so reading and seeking are not supported.

Hash destinations participate in the same shared buffering, fan-out, failure,
and retry behavior as ordinary destinations. A digest therefore represents the
ordered bytes actually accepted by its hash destination. If a partial mirrored
failure leaves a buffered emission pending and the caller retries it, a hash
destination that accepted both attempts includes both observations. The digest
does not attest that ordinary destinations contain identical bytes after a
failed operation.

Hashing runs inline through `HashWriteStream`; TeeHashStream introduces no
worker threads, queues, or payload copies. This keeps the implementation thin
and auditable, accepting that multiple CPU-bound algorithms do not run in
parallel unless the surrounding TeeStream synchronous mode already provides
that execution behavior.

The `HashAlgorithmName` overloads simplify porting and calling from .NET code
that already selects cryptographic hashes. `TeeHashAlgorithm` provides a closed,
documented set of cryptographic and non-cryptographic algorithms and permits
both families in one call. Both input APIs return `TeeHashResults` containing
`TeeHashResult` values, keyed by `TeeHashAlgorithmId`. Implicit conversions let
callers look up results with either input type. Standard cryptographic names
have the same identity through both forms; custom .NET names retain their
runtime-defined support. The shared identifier distinguishes cryptographic
names from checksums with identical text.

Public adapters convert standard cryptographic identifiers between the two
input types. Try-conversion from a non-cryptographic enum member to
`HashAlgorithmName` returns false. Construction, accumulation, and completion
share one internal path after input normalization.
Every constructor requires the algorithm or algorithm sequence as its first
argument. There is no implicit SHA-256 selection. Buffered configuration is
carried by `TeeBufferedStreamOptions`, including buffer size, so the advanced
constructors do not split one layer's settings between an options object and a
loose integer.

Every internal hash destination is finalized and disposed with TeeHashStream,
regardless of `TeeStreamOptions.LeaveOpen`; that option applies only to
caller-provided streams. Once all hash destinations finalize, TeeHashResults
atomically changes from an empty read-only dictionary to a completed dictionary
of immutable TeeHashResult values. Hashes are still published when an ordinary
destination subsequently fails to dispose, while the outer disposal continues
to report that failure normally.

This design preserves TeeBufferedStream semantics and avoids a separate
hashing pipeline, at the cost of making TeeHashStream unsuitable for hashing
reads and making retry-aware digest interpretation important after mirrored
write failures.
