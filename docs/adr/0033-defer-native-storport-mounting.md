# Defer native Storport mounting

Defer implementation of the signed TeeForge Storport virtual miniport while
retaining the current ImDisk-based broker as a non-shipping prototype. The
native design remains the intended Windows 11 direction because the built-in
Virtual Disk API does not accept arbitrary TeeForge formats, but it introduces
a kernel driver, WDK and C++ toolchain requirements, signing and certification,
SCSI and Plug-and-Play obligations, and a substantially larger verification
surface. Record the component boundary, proposed protocol, required command
surface, lifecycle work, and resumption checklist in
[`windows-storport-driver.md`](../windows-storport-driver.md). Do not duplicate
TeeForge media parsing in the future driver and do not expand the ImDisk
transport as if it were the production architecture.
