# Use the TeeForge root namespace

Status: superseded by [ADR 0022](0022-use-feature-namespaces.md).

All public library types will use the flat `TeeForge` namespace rather than mirroring the framework's `System.IO` and `System.IO.Pipelines` namespace hierarchy. This keeps the package's small initial API cohesive and gives it an unmistakable third-party identity, accepting that changing the namespace after release would be a breaking change.
