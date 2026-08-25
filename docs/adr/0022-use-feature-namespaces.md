# Use shallow feature namespaces

TeeForge's public API has grown beyond the small surface assumed by ADR 0002.
Keeping every public type in the root namespace makes unrelated facilities
appear together and increases simple-name collision risk for generic
capabilities such as random-access streams.

## Decision

- Organize public types into the shallow feature namespaces
  `TeeForge.Composition`, `TeeForge.Mirroring`, `TeeForge.Pipelines`,
  `TeeForge.Hashing`, `TeeForge.RandomAccess`, `TeeForge.Sparse`, and
  `TeeForge.ErasureCoding`.
- Keep the root `TeeForge` namespace free of public types.
- Mirror feature boundaries in internal namespaces, such as
  `TeeForge.Pipelines.Internal` and `TeeForge.Sparse.Internal`.
- Use the branded capability names `ITeeRandomAccessStream` and
  `ITeeRangeReadSource`, discovered through `TeeRandomAccess`, to avoid
  collision with platform and third-party abstractions.
- Retain descriptive type prefixes such as `DynamicAllocationStreamOptions`;
  feature namespaces do not justify ambiguous names such as `Options` or
  `Mode`.
- Add another namespace level only when it represents an independently useful
  feature or package boundary.

## Consequences

Consumers import only the feature families they use, and fully qualified names
communicate each type's role. Types that compose across features require
explicit imports, including hashing streams deriving from mirrored buffering
and sparse streams consuming random-access capabilities. This is a source and
binary breaking change, so it is made while all affected APIs remain unshipped.
