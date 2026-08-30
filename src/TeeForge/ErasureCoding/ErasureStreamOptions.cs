namespace TeeForge.ErasureCoding;

/// <summary>Configures format, cache, availability, and ownership for an <see cref="ErasureStream"/>.</summary>
public class ErasureStreamOptions
{
    /// <summary>The benchmark-selected default member block size.</summary>
    public const int DefaultBlockSize = 128 * 1024;

    /// <summary>Gets the default options.</summary>
    public static ErasureStreamOptions Default { get; } = new();

    /// <summary>Initializes options.</summary>
    public ErasureStreamOptions(
        ErasureStreamFormat format = ErasureStreamFormat.SelfDescribing,
        bool requireAllMembers = true,
        bool leaveOpen = false,
        long maximumCacheBytes = 64L * 1024 * 1024,
        int readAheadBlockCount = 1)
    {
        if (!Enum.IsDefined(format))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCacheBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(readAheadBlockCount);

        Format = format;
        RequireAllMembers = requireAllMembers;
        LeaveOpen = leaveOpen;
        MaximumCacheBytes = maximumCacheBytes;
        ReadAheadBlockCount = readAheadBlockCount;
    }

    /// <summary>Gets the member format used when creating or opening a stream.</summary>
    public ErasureStreamFormat Format { get; }

    /// <summary>Gets whether opening requires every configured member.</summary>
    public bool RequireAllMembers { get; }

    /// <summary>Gets whether disposal leaves member streams open.</summary>
    public bool LeaveOpen { get; }

    /// <summary>Gets the approximate maximum bytes retained by the unified block cache.</summary>
    public long MaximumCacheBytes { get; }

    /// <summary>Gets the number of logical data blocks speculatively cached after a read.</summary>
    public int ReadAheadBlockCount { get; }
}
