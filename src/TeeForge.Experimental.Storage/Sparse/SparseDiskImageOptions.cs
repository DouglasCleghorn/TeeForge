namespace TeeForge.Experimental.Storage.Sparse;

/// <summary>Provides immutable options for opening or creating a <see cref="SparseDiskImage"/>.</summary>
public class SparseDiskImageOptions
{
    /// <summary>Gets the default options.</summary>
    public static SparseDiskImageOptions Default { get; } = new();

    /// <summary>Initializes a new options instance.</summary>
    /// <param name="leaveOpen">Whether disposing the wrapper leaves the backing stream open.</param>
    /// <param name="readOnly">Whether to force read-only operation even when the backing stream is writable.</param>
    /// <param name="freeBlockQueueCapacity">Maximum known-free physical block offsets retained in memory; zero disables discovery.</param>
    /// <param name="freeBlockQueueLowWatermark">Queue count below which background discovery is requested.</param>
    public SparseDiskImageOptions(
        bool leaveOpen = false,
        bool readOnly = false,
        int freeBlockQueueCapacity = 4096,
        int freeBlockQueueLowWatermark = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(freeBlockQueueCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(freeBlockQueueLowWatermark);

        if (freeBlockQueueLowWatermark > freeBlockQueueCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(freeBlockQueueLowWatermark),
                "The low watermark cannot exceed queue capacity.");
        }

        LeaveOpen = leaveOpen;
        ReadOnly = readOnly;
        FreeBlockQueueCapacity = freeBlockQueueCapacity;
        FreeBlockQueueLowWatermark = freeBlockQueueLowWatermark;
    }

    /// <summary>Gets whether disposing the wrapper leaves the backing stream open.</summary>
    public bool LeaveOpen { get; }

    /// <summary>Gets whether opening forces read-only operation.</summary>
    public bool ReadOnly { get; }

    /// <summary>Gets the maximum count of known-free physical block offsets retained in memory.</summary>
    public int FreeBlockQueueCapacity { get; }

    /// <summary>Gets the queue count below which background discovery is requested.</summary>
    public int FreeBlockQueueLowWatermark { get; }
}
