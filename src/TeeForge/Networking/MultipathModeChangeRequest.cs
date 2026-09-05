namespace TeeForge.Networking;

/// <summary>Contains the validated payload of a control-plane mode request.</summary>
public class MultipathModeChangeRequest
{
    internal MultipathModeChangeRequest(MultipathStreamMode mode, int dataShardCount, int parityShardCount)
    {
        Mode = mode;
        DataShardCount = dataShardCount;
        ParityShardCount = parityShardCount;
    }

    /// <summary>Gets the requested mode; the application authorizes and applies it at the sender.</summary>
    public MultipathStreamMode Mode { get; }
    /// <summary>Gets the erasure data-shard count, or zero for other modes.</summary>
    public int DataShardCount { get; }
    /// <summary>Gets the erasure parity-shard count, or zero for other modes.</summary>
    public int ParityShardCount { get; }
}
