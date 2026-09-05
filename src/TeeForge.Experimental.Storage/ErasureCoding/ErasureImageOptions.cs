using TeeForge.ErasureCoding;

namespace TeeForge.Experimental.Storage.ErasureCoding;

/// <summary>Configures format, cache, availability, and ownership for an <see cref="ErasureImage"/>.</summary>
public class ErasureImageOptions : ErasureStreamOptions
{
    /// <summary>Gets the default options.</summary>
    public new static ErasureImageOptions Default { get; } = new();

    /// <summary>Initializes options.</summary>
    public ErasureImageOptions(
        ErasureImageFormat format = ErasureImageFormat.SelfDescribing,
        bool requireAllMembers = true,
        bool leaveOpen = false,
        long maximumCacheBytes = 64L * 1024 * 1024,
        int readAheadBlockCount = 1)
        : base(requireAllMembers, leaveOpen, maximumCacheBytes, readAheadBlockCount)
    {
        if (!Enum.IsDefined(format))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCacheBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(readAheadBlockCount);

        Format = format;
    }

    /// <summary>Gets the member format used when creating or opening a stream.</summary>
    public ErasureImageFormat Format { get; }

}
