namespace TeeForge.Networking;

/// <summary>Captures one consistent view of a sender's local configuration, membership, and lifecycle.</summary>
/// <remarks>Protection describes local path capacity. It is not a delivery acknowledgement or a remote health guarantee.</remarks>
public class MultipathSenderStatus
{
    internal MultipathSenderStatus(MultipathStreamMode desiredMode, MultipathStreamMode effectiveMode,
        int pathCount, int dataShardCount, int parityShardCount, ulong membershipEpoch,
        MultipathSenderState state, MultipathProtectionState protection)
    {
        DesiredMode = desiredMode;
        EffectiveMode = effectiveMode;
        PathCount = pathCount;
        ErasureDataShardCount = dataShardCount;
        ErasureParityShardCount = parityShardCount;
        MembershipEpoch = membershipEpoch;
        State = state;
        Protection = protection;
    }

    /// <summary>Gets the requested distribution mode.</summary>
    public MultipathStreamMode DesiredMode { get; }
    /// <summary>Gets the mode selected by the captured configuration and path count.</summary>
    public MultipathStreamMode EffectiveMode { get; }
    /// <summary>Gets the captured number of locally active paths.</summary>
    public int PathCount { get; }
    /// <summary>Gets the configured data-shard count.</summary>
    public int ErasureDataShardCount { get; }
    /// <summary>Gets the configured parity-shard count.</summary>
    public int ErasureParityShardCount { get; }
    /// <summary>Gets the captured membership epoch.</summary>
    public ulong MembershipEpoch { get; }
    /// <summary>Gets the sender lifecycle state.</summary>
    public MultipathSenderState State { get; }
    /// <summary>Gets the locally available protection for further publication.</summary>
    public MultipathProtectionState Protection { get; }
}

/// <summary>Identifies the lifecycle state of a multipath sender.</summary>
public enum MultipathSenderState
{
    /// <summary>The sender accepts writes and paths.</summary>
    Open,
    /// <summary>The sender is publishing remaining data and logical EOF.</summary>
    Completing,
    /// <summary>The sender has published logical EOF.</summary>
    Completed,
    /// <summary>The sender cannot continue because a data operation failed irrecoverably.</summary>
    Faulted,
    /// <summary>The sender has been disposed.</summary>
    Disposed,
}

/// <summary>Describes local capacity for future groups, independently of desired distribution mode.</summary>
public enum MultipathProtectionState
{
    /// <summary>No paths are available, or the sender is completed, faulted, or disposed.</summary>
    Unavailable,
    /// <summary>Groups have no redundant copy: one mirrored path or RAID 0 at any path count.</summary>
    Unprotected,
    /// <summary>At least two paths are available for mirrored groups.</summary>
    Mirrored,
    /// <summary>Enough paths are available for all configured data and parity shards.</summary>
    ErasureProtected,
}
