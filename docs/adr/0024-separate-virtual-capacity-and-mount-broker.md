# Separate virtual capacity and mount broker

Mountable TeeForge images persist a positive 4 KiB-aligned virtual capacity independently of allocation-derived stream length, require matching capacity throughout a differencing chain, and reject writes or trims beyond that boundary before I/O. Windows mounting uses a separate user-mode broker that translates block requests to TeeForge random access, trim, and flush operations, initially through an explicitly installed ImDisk proxy and eventually through a signed TeeForge Storport virtual miniport, leaving all format behavior in the library. Version one provides nondestructive, session-scoped ordinary data-disk mounts only, with explicit shell verbs and no automatic driver installation, disk initialization, reboot remount, or boot/system/dump support; because no format has been released, version 1.0 is revised in place and prototype images receive no compatibility or migration guarantee.

ADR 0033 defers the Storport implementation and classifies ImDisk as a
non-shipping prototype. The intended native component boundary and resumption
notes are preserved in `docs/windows-storport-driver.md`.
