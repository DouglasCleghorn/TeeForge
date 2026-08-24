# Adapt the .NET BufferedStream source for TeeBufferedStream

`TeeBufferedStream` is an adaptation of Microsoft's MIT-licensed
`System.IO.BufferedStream`, not a thin wrapper around the framework class. The
import uses the repository's pinned `dotnet/runtime` `release/10.0` commit,
`4271d88e0aebf3d04f188f1334c2220d80555ef6`, and retains the .NET Foundation
license header and relevant adapted compatibility tests.

The Microsoft lazy shared-buffer engine, large-operation bypass, shadow-buffer
heuristic, seek bookkeeping, and synchronous/asynchronous paths are preserved.
Its underlying emission target is a TeeStream configured with the caller's
destinations and options, so all existing mirror consistency, failure
aggregation, fan-out, and ownership rules remain centralized and auditable.
Capabilities are snapshotted once for the buffering hot paths, consistent with
the conventional Stream contract that they remain stable while a stream is
open.
