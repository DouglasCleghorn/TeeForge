# Deferred native Windows virtual-disk driver

Status: **deferred on 2026-08-26**. This document records the intended native
Windows 11 integration so work can resume without repeating the architecture
investigation. It is a design note, not an implemented or supported feature.

## Why a driver is required

The Windows Virtual Disk API is not an extension point for arbitrary image
formats. Its public virtual-storage device identifiers cover ISO, VHD, and
VHDX. TeeForge must therefore expose `.tfdisk` and `.tfdiff` through the Windows
storage stack if it is to appear as a native disk without first converting the
image to VHDX.

The intended integration is a Storport virtual miniport. Storport supplies the
standard disk-class, Plug and Play, power, queueing, and storage-management
layers above the miniport. The TeeForge miniport presents one or more ordinary
SCSI direct-access LUNs and translates their requests into a private,
versioned exchange with a user-mode broker.

The existing ImDisk broker is only a prototype transport. Do not invest in
shipping, benchmarking, or silently installing ImDisk as the long-term Windows
11 solution.

## Component boundary

The kernel driver must remain format-agnostic:

1. `TeeForge.VirtualDisk.sys` exposes virtual disk LUNs and implements the
   required Storport and SCSI contracts.
2. A privileged `TeeForge.Mount` broker owns each image session and resolves a
   complete `.tfdiff` parent chain.
3. The broker opens `DynamicAllocationStream` or `DifferencingStream` from the
   TeeForge library and services logical I/O through `ITeeRandomAccessStream`,
   `TrimAsync`, and `FlushAsync`.
4. Windows Disk, Partition, Volume, Mount Manager, and filesystem drivers see a
   normal 4096-byte-sector disk. They never parse a TeeForge file format.

This boundary is non-negotiable. Moving BAT, presence, parent-chain, journal,
or recovery logic into kernel mode would duplicate the library and enlarge the
trusted crash surface.

## Proposed driver/broker protocol

Define the protocol once in a C-compatible shared header and mirror it with a
layout-tested managed codec. Every message should contain a magic value,
protocol major/minor version, header and payload lengths, session ID, request
ID, operation, flags, and status. Multi-byte fields use little-endian encoding;
all offsets and lengths are unsigned and validated before conversion to .NET
`long` values.

Required control operations:

- identify adapter and negotiate protocol version;
- attach a session with stable image ID, virtual capacity, sector size, and
  read-only flags;
- detach a session and fail or drain outstanding requests;
- report the adapter/path/target/LUN identity needed to locate the resulting
  physical disk and volume;
- query session state for recovery after a broker failure.

Required data operations:

- read at an absolute byte offset;
- write at an absolute byte offset;
- flush;
- unmap one or more validated byte ranges;
- orderly close and surprise-disconnect notification.

The likely transport is `IOCTL_MINIPORT_PROCESS_SERVICE_IRP`, which Storport
provides for reverse callbacks between a virtual miniport and a user-mode
service. A separate vendor `IOCTL_SCSI_MINIPORT` control exchange can identify
the adapter and manage LUN attachment. Finalize cancellation, request ownership,
buffering, queue depth, and completion ordering only while both sides can be
built and exercised under Driver Verifier.

No logical WRITE, UNMAP, or FLUSH may be acknowledged to Storport before the
corresponding library operation reaches its documented durability boundary.
For a differencing image, no data request may open the parent writable or send
any ordinary I/O upstream.

## Minimum SCSI behavior

The virtual LUN needs a deliberately small direct-access command surface. At a
minimum, account for:

- INQUIRY and vital product data used for stable identification;
- TEST UNIT READY and REQUEST SENSE;
- READ CAPACITY (10) and READ CAPACITY (16);
- READ (10/16) and WRITE (10/16);
- SYNCHRONIZE CACHE (10/16);
- UNMAP with complete descriptor-list preflight;
- MODE SENSE, MODE SELECT, REPORT LUNS, and START STOP UNIT behavior expected by
  the Windows disk and volume stacks;
- write-protect reporting for read-only sessions;
- unsupported-command sense data, reset, cancellation, power transition, and
  surprise-removal behavior.

Advertise a 4096-byte logical and physical sector initially. Transfer lengths,
logical block addresses, UNMAP ranges, and capacity arithmetic must be checked
for overflow and bounds before a request crosses into user mode. The driver
must never expose partially initialized read buffers to the kernel.

## Windows lifecycle work

Attaching a LUN is only the first half of mounting. The broker must also:

- wait for disk-class enumeration and identify the correct `PhysicalDrive` by
  adapter/path/target/LUN or stable VPD identity;
- distinguish an uninitialized disk from a disk with partitions and volumes;
- assign an explicitly requested unused drive letter only after the target
  volume exists;
- leave initialization, partitioning, filesystem creation, and formatting as
  separate explicit administrative actions;
- remove mount points, flush the leaf, detach the LUN, and reconcile stale
  broker state during unmount or recovery.

Version one remains session-scoped and excludes boot, system, pagefile,
hibernation, crash-dump, and reboot-persistent disks.

## Toolchain, testing, and signing

As of 2026-08-26, resuming implementation requires a compatible Visual Studio
C++ toolset, Windows SDK, and WDK. The current development machine has the .NET
SDK and Windows SDK headers but does not have the Visual C++ linker or WDK
Storport headers and libraries.

Development should start with an x64 test-signed package on a disposable Windows
11 VM or dedicated test machine with kernel debugging available. Do not make
enabling test-signing or weakening Secure Boot an automatic product action.
Add ARM64 after the x64 protocol and lifecycle are stable.

Public distribution requires Microsoft-accepted kernel signing. Plan for a
Hardware Dev Center account and appropriate code-signing certificate, then HLK
storage testing for a production-quality release. Attestation signing can help
with limited testing scenarios but is not a substitute for Windows certification
or retail Windows Update distribution.

Required verification before calling the driver mountable:

- compile with driver code analysis and warning-as-error settings;
- Static Driver Verifier and Driver Verifier runs;
- clean install, upgrade, rollback, uninstall, reboot, sleep, and surprise-kill
  tests on supported Windows 11 builds;
- malformed and oversized protocol/SCSI request fuzzing;
- broker crash and restart at every request phase;
- read-only enforcement and parent immutability tests;
- filesystem workloads, UNMAP, forced flush, power-loss simulation, and
  multi-gigabyte boundary tests;
- an actual signed-package installation test with Secure Boot enabled.

## Resume checklist and open decisions

When this work resumes:

1. Install a matching Visual C++/SDK/WDK toolchain or use the Enterprise WDK.
2. Confirm the first target is x64 test signing, followed by ARM64 and production
   signing.
3. Allocate permanent protocol, hardware, service, and interface GUIDs.
4. Decide maximum transfer size and initial outstanding service-IRP count from
   measured filesystem workloads rather than copying the ImDisk buffer size.
5. Specify cancellation and completion state machines before writing the
   miniport queue code.
6. Create the shared protocol header and managed layout tests.
7. Implement one read-only LUN first; add write, flush, UNMAP, multiple LUNs,
   and mount-point automation in that order.
8. Keep the driver project separate from the TeeForge stream library and keep
   all format semantics in the library.

Primary references:

- [Implementing a Storport virtual miniport driver](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/initialization-of-storage-virtual-miniport-drivers)
- [`IOCTL_MINIPORT_PROCESS_SERVICE_IRP`](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddscsi/ni-ntddscsi-ioctl_miniport_process_service_irp)
- [Windows storage driver architecture](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/storage-driver-architecture)
- [Virtual storage types supported by the Virtual Disk API](https://learn.microsoft.com/en-us/windows/win32/api/virtdisk/ns-virtdisk-virtual_storage_type)
- [Install the WDK](https://learn.microsoft.com/en-us/windows-hardware/drivers/download-the-wdk)
- [Windows driver signing](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/windows-driver-signing-tutorial)
- [Driver signing options](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/driver-signing-offerings)
