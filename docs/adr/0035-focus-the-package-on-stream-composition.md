# Focus the package on stream composition

TeeForge provides ordinary Stream and pipeline composition with optional
position-independent I/O. Persistent storage formats, journals, configuration,
and volume maintenance belong to a separate, unpublished assembly in the same
repository. Core build, test, benchmarks, AOT smoke, and package verification
must not depend on that assembly.

The core ErasureStream is headerless and uses caller-supplied geometry and
member order. Its shared I/O implementation and codec are reused internally
by the experimental image implementation; the core public API exposes no
persistent identifiers, header parser, or parity-maintenance operations.

The existing experimental formats retain their byte encodings and tests.
Namespaces and names explicitly identify their experimental image/volume
responsibilities. They are not production APIs or part of the normal release.

This supersedes the storage namespace examples in ADR 0022. The remaining
shallow feature namespaces and stream capability conventions still apply.
