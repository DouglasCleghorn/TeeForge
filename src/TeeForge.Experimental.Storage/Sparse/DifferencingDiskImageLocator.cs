namespace TeeForge.Experimental.Storage.Sparse;

/// <summary>Describes the immediate parent recorded by a differencing image.</summary>
public sealed class DifferencingDiskImageLocator
{
    internal DifferencingDiskImageLocator(
        Guid baseId,
        Guid baseDataWriteId,
        long virtualCapacity,
        int blockSize,
        string? parentPathHint)
    {
        BaseId = baseId;
        BaseDataWriteId = baseDataWriteId;
        VirtualCapacity = virtualCapacity;
        BlockSize = blockSize;
        ParentPathHint = parentPathHint;
    }

    /// <summary>Gets the stable identifier of the immediate parent.</summary>
    public Guid BaseId { get; }

    /// <summary>Gets the required parent data generation.</summary>
    public Guid BaseDataWriteId { get; }

    /// <summary>Gets the virtual capacity shared by the chain.</summary>
    public long VirtualCapacity { get; }

    /// <summary>Gets the allocation block size shared by the chain.</summary>
    public int BlockSize { get; }

    /// <summary>Gets the optional relative path hint for the immediate parent.</summary>
    public string? ParentPathHint { get; }
}
