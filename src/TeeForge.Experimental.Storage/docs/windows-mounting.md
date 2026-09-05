# Mounting TeeForge disks on Windows

`tools/TeeForge.Mount` is a separate Windows executable that exposes TeeForge
virtual-disk streams through an ImDisk shared-memory proxy. Disk parsing,
parent validation, reads, writes, trim, and flush remain in the TeeForge
library; the tool only translates device requests and manages mount sessions.

This is a non-shipping prototype. Native Windows 11 integration through a
signed Storport virtual miniport is intentionally deferred; its design and
resumption checklist are recorded in
[the Storport driver note](windows-storport-driver.md). Do not treat the current
ImDisk dependency as the production mounting architecture.

## Image extensions

- `.tfdisk` is a standalone sparse `SparseDiskImage` image.
- `.tfdiff` is a `DifferencingDiskImage` image whose immediate parent is another
  `.tfdisk` or `.tfdiff` image.

The extension selects a parser but is not trusted as validation. Every opened
image is still checked for its format signature, checksums, identity, geometry,
and parent binding. The broker obtains `.tfdiff` parent information through the
library's validated locator API; it does not decode TeeForge media structures
itself.

## Prerequisites and limits

Build the repository with the .NET 10 SDK. Install ImDisk yourself and place
`imdisk.exe` on `PATH`; TeeForge never downloads, installs, upgrades, or removes
a driver. Mount and unmount may require an elevated terminal.

The first broker implementation presents a 4096-byte logical sector. A supplied
drive letter must be unused; when omitted, TeeForge chooses the first unused
letter from Z: downward and persists the actual choice in session state. It
supports session-scoped data-disk mounts only. It does not initialize a
partition table, create a filesystem,
format a volume, mount at boot, or support boot/system/dump disks. Those remain
separate, explicit Windows administration operations.

ImDisk's upstream project describes itself as an old compatibility-oriented
design and does not recommend it for recent Windows. It is the first TeeForge
transport, not the intended long-term signed virtual-disk driver.

## Build and inspect

```powershell
dotnet build tools\TeeForge.Mount\TeeForge.Mount.csproj
tools\TeeForge.Mount\bin\Debug\net10.0-windows\teeforge-mount.exe inspect .\disk.tfdisk
```

Inspecting an image records its stable ID in the per-user parent catalog. For a
difference image, supply `--parent` when the relative locator hint and catalog
cannot resolve the immediate parent:

```powershell
teeforge-mount inspect .\leaf.tfdiff --parent .\parent.tfdiff
```

## Mount lifecycle

```powershell
teeforge-mount mount .\disk.tfdisk --mount-point T:
teeforge-mount mount .\leaf.tfdiff --parent .\base.tfdisk --mount-point U:
teeforge-mount mount .\leaf.tfdiff --mount-point V: --read-only

teeforge-mount list
teeforge-mount status <mount-id>
teeforge-mount unmount T:
```

A writable difference mount opens only the leaf writable. Every resolved parent
is opened read-only, and ordinary leaf I/O never writes upstream. Parent IDs,
data-write IDs, capacity, and block size are validated while opening the chain.

Explorer verbs are a separate opt-in per-user registry operation:

```powershell
teeforge-mount shell install
teeforge-mount shell uninstall
```

Installation adds Inspect, Mount, and Mount read-only verbs for both extensions.
It does not install ImDisk or create a machine-wide association.

## Trim behavior

The shared-memory proxy advertises ImDisk UNMAP and ZERO support for writable
mounts. Each request contains one or more Windows `DEVICE_DATA_SET_RANGE`
records. The broker validates every range against `VirtualCapacity` before
changing the image, translates live portions to `TrimAsync`, and flushes before
replying.

The shared-memory request set has no separate flush operation in this broker
profile, so WRITE is also flushed before acknowledgement. This favors a clear
durability boundary over initial throughput; mount benchmarking remains
deferred until the functional path is complete.

For `.tfdiff`, a complete allocation block becomes the erased BAT state and a
partial range is materialized in 4096-byte grains. Both operations mask parent
bytes with zero; a later read cannot fall through to upstream data. Ranges that
lie entirely in the already-sparse capacity tail are successful no-ops.

## Parent resolution

The tool resolves an immediate `.tfdiff` parent in this order:

1. the explicit `--parent` path;
2. the image's relative UTF-8 parent locator hint;
3. the per-user catalog entry for the stored base ID.

Resolution is recursive. A locator is never sufficient by itself: opening still
rejects a parent whose stable ID, data-write ID, virtual capacity, or block size
does not match the child. Repeated canonical paths are rejected as a parent-chain
cycle before another recursive open is attempted.
