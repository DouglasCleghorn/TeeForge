using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Naming", "CA1710:Identifiers should have correct suffix", Justification = "Experimental images and volumes retain Stream interoperability while naming their persistent storage responsibilities.", Scope = "type", Target = "~T:TeeForge.Experimental.Storage.Sparse.SparseDiskImage")]
[assembly: SuppressMessage("Naming", "CA1710:Identifiers should have correct suffix", Justification = "Experimental images and volumes retain Stream interoperability while naming their persistent storage responsibilities.", Scope = "type", Target = "~T:TeeForge.Experimental.Storage.Sparse.DifferencingDiskImage")]
[assembly: SuppressMessage("Naming", "CA1710:Identifiers should have correct suffix", Justification = "Experimental images and volumes retain Stream interoperability while naming their persistent storage responsibilities.", Scope = "type", Target = "~T:TeeForge.Experimental.Storage.ErasureCoding.ErasureCodedVolume")]
