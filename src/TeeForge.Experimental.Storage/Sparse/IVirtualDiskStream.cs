using System.Diagnostics.CodeAnalysis;

namespace TeeForge.Experimental.Storage.Sparse;

/// <summary>Exposes immutable geometry shared by TeeForge virtual-disk streams.</summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "The interface is a capability implemented by Stream-derived virtual disks.")]
public interface IVirtualDiskStream : IStreamIdentity
{
    /// <summary>Gets the allocation block size.</summary>
    int BlockSize { get; }

    /// <summary>Gets the immutable logical capacity.</summary>
    long VirtualCapacity { get; }
}
