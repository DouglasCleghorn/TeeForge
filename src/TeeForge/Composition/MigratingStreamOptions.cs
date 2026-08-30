namespace TeeForge.Composition;

/// <summary>Provides immutable options for a <see cref="MigratingStream"/>.</summary>
public class MigratingStreamOptions
{
    private const int DefaultBufferSize = 81920;

    /// <summary>Gets the default migration options.</summary>
    public static MigratingStreamOptions Default { get; } = new();

    /// <summary>Initializes a new migration-options instance.</summary>
    /// <param name="leaveSourceOpen">Whether disposal leaves the source stream open.</param>
    /// <param name="leaveDestinationOpen">Whether disposal leaves the destination stream open.</param>
    /// <param name="truncateSourceOnCompletion">
    /// Whether successful migration truncates the source after the destination is flushed.
    /// </param>
    /// <param name="bufferSize">The maximum number of bytes copied in one migration quantum.</param>
    public MigratingStreamOptions(
        bool leaveSourceOpen = false,
        bool leaveDestinationOpen = false,
        bool truncateSourceOnCompletion = false,
        int bufferSize = DefaultBufferSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        LeaveSourceOpen = leaveSourceOpen;
        LeaveDestinationOpen = leaveDestinationOpen;
        TruncateSourceOnCompletion = truncateSourceOnCompletion;
        BufferSize = bufferSize;
    }

    /// <summary>Gets whether disposal leaves the source stream open.</summary>
    public bool LeaveSourceOpen { get; }

    /// <summary>Gets whether disposal leaves the destination stream open.</summary>
    public bool LeaveDestinationOpen { get; }

    /// <summary>Gets whether successful migration truncates the source.</summary>
    public bool TruncateSourceOnCompletion { get; }

    /// <summary>Gets the maximum number of bytes copied in one migration quantum.</summary>
    public int BufferSize { get; }
}
