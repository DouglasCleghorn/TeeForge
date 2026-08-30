namespace TeeForge.Networking;

/// <summary>Provides immutable framing, redundancy, ownership, and outage options for multipath streams.</summary>
public class MultipathStreamOptions
{
    /// <summary>The default maximum payload carried by one path frame.</summary>
    public const int DefaultFramePayloadSize = 16 * 1024;

    /// <summary>Initializes a new options instance.</summary>
    /// <param name="mode">The initially desired distribution mode.</param>
    /// <param name="framePayloadSize">The positive maximum payload size of one path frame.</param>
    /// <param name="erasureDataShardCount">The number of systematic shards in an erasure group.</param>
    /// <param name="erasureParityShardCount">The number of parity shards in an erasure group.</param>
    /// <param name="pathQueueCapacity">The maximum number of queued frames retained for one path.</param>
    /// <param name="maximumReorderGroups">The maximum distance accepted ahead of the next logical group.</param>
    /// <param name="pathAvailabilityTimeout">
    /// The maximum time an operation waits for a path, or <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </param>
    /// <param name="leaveOpen">Whether disposing a multipath object leaves its supplied streams open.</param>
    public MultipathStreamOptions(
        MultipathStreamMode mode = MultipathStreamMode.Raid1,
        int framePayloadSize = DefaultFramePayloadSize,
        int erasureDataShardCount = 4,
        int erasureParityShardCount = 2,
        int pathQueueCapacity = 8,
        int maximumReorderGroups = 1024,
        TimeSpan? pathAvailabilityTimeout = null,
        bool leaveOpen = false)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framePayloadSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(framePayloadSize, 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(erasureDataShardCount, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(erasureParityShardCount, 1);
        if ((long)erasureDataShardCount + erasureParityShardCount > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(erasureParityShardCount));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pathQueueCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumReorderGroups);

        TimeSpan resolvedTimeout = pathAvailabilityTimeout ?? Timeout.InfiniteTimeSpan;
        if (resolvedTimeout != Timeout.InfiniteTimeSpan && resolvedTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pathAvailabilityTimeout));
        }

        Mode = mode;
        FramePayloadSize = framePayloadSize;
        ErasureDataShardCount = erasureDataShardCount;
        ErasureParityShardCount = erasureParityShardCount;
        PathQueueCapacity = pathQueueCapacity;
        MaximumReorderGroups = maximumReorderGroups;
        PathAvailabilityTimeout = resolvedTimeout;
        LeaveOpen = leaveOpen;
    }

    /// <summary>Gets the initially desired distribution mode.</summary>
    public MultipathStreamMode Mode { get; }

    /// <summary>Gets the maximum payload size of one path frame.</summary>
    public int FramePayloadSize { get; }

    /// <summary>Gets the number of systematic shards in an erasure group.</summary>
    public int ErasureDataShardCount { get; }

    /// <summary>Gets the number of parity shards in an erasure group.</summary>
    public int ErasureParityShardCount { get; }

    /// <summary>Gets the maximum number of queued frames retained for one path.</summary>
    public int PathQueueCapacity { get; }

    /// <summary>Gets the maximum accepted distance ahead of the next logical group.</summary>
    public int MaximumReorderGroups { get; }

    /// <summary>Gets the maximum time an operation waits for at least one path.</summary>
    public TimeSpan PathAvailabilityTimeout { get; }

    /// <summary>Gets whether supplied streams remain open when their multipath owner is disposed.</summary>
    public bool LeaveOpen { get; }
}
