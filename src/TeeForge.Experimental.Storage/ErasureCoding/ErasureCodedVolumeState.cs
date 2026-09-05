namespace TeeForge.Experimental.Storage.ErasureCoding;

/// <summary>Describes the externally observable availability of an erasure-coded stream.</summary>
public enum ErasureCodedVolumeStatus
{
    /// <summary>Every configured member is online.</summary>
    Healthy,

    /// <summary>The stream remains usable, but at least one member is not online.</summary>
    Degraded,

    /// <summary>Fewer than the required number of readable members remain.</summary>
    Unavailable,

    /// <summary>An ambiguity or unrecoverable consistency failure faulted the stream.</summary>
    Faulted,

    /// <summary>The stream has been disposed.</summary>
    Disposed,
}

/// <summary>Describes the observed condition of one configured member.</summary>
public enum ErasureMemberStatus
{
    /// <summary>The member is available and current.</summary>
    Online,

    /// <summary>The member was not supplied or stopped responding.</summary>
    Missing,

    /// <summary>The member is available but does not contain the current generation.</summary>
    Stale,

    /// <summary>The member returned invalid metadata or data.</summary>
    Corrupt,

    /// <summary>The member is being reconstructed.</summary>
    Rebuilding,

    /// <summary>The member no longer participates in the active configuration.</summary>
    Retired,
}

/// <summary>Identifies whether a configured member stores systematic data or parity.</summary>
public enum ErasureMemberRole
{
    /// <summary>A systematic data member.</summary>
    Data,

    /// <summary>A Reed-Solomon parity member.</summary>
    Parity,
}

/// <summary>Contains cumulative I/O and sampled latency statistics for one member.</summary>
public sealed class ErasureMemberPerformance
{
    internal ErasureMemberPerformance(
        long bytesRead,
        long bytesWritten,
        long readOperations,
        long writeOperations,
        long flushOperations,
        long reconstructionBytes,
        long errors,
        long sampledReads,
        long sampledWrites,
        long sampledFlushes,
        double readLatencyMilliseconds,
        double writeLatencyMilliseconds,
        double flushLatencyMilliseconds,
        double readThroughputBytesPerSecond,
        double writeThroughputBytesPerSecond,
        double maximumSampledLatencyMilliseconds,
        long[] latencyBuckets)
    {
        BytesRead = bytesRead;
        BytesWritten = bytesWritten;
        ReadOperations = readOperations;
        WriteOperations = writeOperations;
        FlushOperations = flushOperations;
        ReconstructionBytes = reconstructionBytes;
        Errors = errors;
        SampledReads = sampledReads;
        SampledWrites = sampledWrites;
        SampledFlushes = sampledFlushes;
        ReadLatencyMilliseconds = readLatencyMilliseconds;
        WriteLatencyMilliseconds = writeLatencyMilliseconds;
        FlushLatencyMilliseconds = flushLatencyMilliseconds;
        ReadThroughputBytesPerSecond = readThroughputBytesPerSecond;
        WriteThroughputBytesPerSecond = writeThroughputBytesPerSecond;
        MaximumSampledLatencyMilliseconds = maximumSampledLatencyMilliseconds;
        LatencyBuckets = Array.AsReadOnly(latencyBuckets);
    }

    /// <summary>Gets cumulative bytes read from the member.</summary>
    public long BytesRead { get; }

    /// <summary>Gets cumulative bytes written to the member.</summary>
    public long BytesWritten { get; }

    /// <summary>Gets completed read operations.</summary>
    public long ReadOperations { get; }

    /// <summary>Gets completed write operations.</summary>
    public long WriteOperations { get; }

    /// <summary>Gets completed flush operations.</summary>
    public long FlushOperations { get; }

    /// <summary>Gets bytes reconstructed because this member's data was unavailable or invalid.</summary>
    public long ReconstructionBytes { get; }

    /// <summary>Gets member I/O failures observed by the stream.</summary>
    public long Errors { get; }

    /// <summary>Gets sampled read operations.</summary>
    public long SampledReads { get; }

    /// <summary>Gets sampled write operations.</summary>
    public long SampledWrites { get; }

    /// <summary>Gets sampled flush operations.</summary>
    public long SampledFlushes { get; }

    /// <summary>Gets exponentially weighted mean read latency.</summary>
    public double ReadLatencyMilliseconds { get; }

    /// <summary>Gets exponentially weighted mean write latency.</summary>
    public double WriteLatencyMilliseconds { get; }

    /// <summary>Gets exponentially weighted mean flush latency.</summary>
    public double FlushLatencyMilliseconds { get; }

    /// <summary>Gets exponentially weighted mean sampled read throughput.</summary>
    public double ReadThroughputBytesPerSecond { get; }

    /// <summary>Gets exponentially weighted mean sampled write throughput.</summary>
    public double WriteThroughputBytesPerSecond { get; }

    /// <summary>Gets the maximum sampled operation latency.</summary>
    public double MaximumSampledLatencyMilliseconds { get; }

    /// <summary>Gets base-two microsecond latency buckets covering [1,2) through 32768+ microseconds.</summary>
    public IReadOnlyList<long> LatencyBuckets { get; }
}

/// <summary>Contains condition and performance information for one configured member.</summary>
public sealed class ErasureMemberState
{
    internal ErasureMemberState(
        Guid memberId,
        int position,
        ErasureMemberRole role,
        ErasureMemberStatus status,
        bool canRead,
        bool canWrite,
        ErasureMemberPerformance performance)
    {
        MemberId = memberId;
        Position = position;
        Role = role;
        Status = status;
        CanRead = canRead;
        CanWrite = canWrite;
        Performance = performance;
    }

    /// <summary>Gets the persistent member identifier.</summary>
    public Guid MemberId { get; }

    /// <summary>Gets the persistent position in the codeword.</summary>
    public int Position { get; }

    /// <summary>Gets the member's role.</summary>
    public ErasureMemberRole Role { get; }

    /// <summary>Gets the member's observed condition.</summary>
    public ErasureMemberStatus Status { get; }

    /// <summary>Gets whether the member currently supports reads.</summary>
    public bool CanRead { get; }

    /// <summary>Gets whether the member currently supports writes.</summary>
    public bool CanWrite { get; }

    /// <summary>Gets cumulative performance statistics.</summary>
    public ErasureMemberPerformance Performance { get; }
}

/// <summary>Represents an immutable point-in-time stream health snapshot.</summary>
public sealed class ErasureCodedVolumeState
{
    internal ErasureCodedVolumeState(
        DateTimeOffset observedAt,
        ErasureCodedVolumeStatus status,
        bool isReadOnly,
        bool canRead,
        bool canWrite,
        int readQuorum,
        int writeQuorum,
        ErasureMemberState[] members)
    {
        ObservedAt = observedAt;
        Status = status;
        IsReadOnly = isReadOnly;
        CanRead = canRead;
        CanWrite = canWrite;
        ReadQuorum = readQuorum;
        WriteQuorum = writeQuorum;
        Members = Array.AsReadOnly(members);
    }

    /// <summary>Gets when this snapshot was captured.</summary>
    public DateTimeOffset ObservedAt { get; }

    /// <summary>Gets aggregate stream status.</summary>
    public ErasureCodedVolumeStatus Status { get; }

    /// <summary>Gets whether the stream was opened read-only.</summary>
    public bool IsReadOnly { get; }

    /// <summary>Gets whether the current membership has read quorum.</summary>
    public bool CanRead { get; }

    /// <summary>Gets whether the current membership has write quorum and writes are enabled.</summary>
    public bool CanWrite { get; }

    /// <summary>Gets the number of members required to decode.</summary>
    public int ReadQuorum { get; }

    /// <summary>Gets the number of current journal and home copies required to commit a write.</summary>
    public int WriteQuorum { get; }

    /// <summary>Gets members ordered by persistent position.</summary>
    public IReadOnlyList<ErasureMemberState> Members { get; }
}

/// <summary>Supplies the previous and current snapshots for a health transition.</summary>
public sealed class ErasureCodedVolumeStateChangedEventArgs : EventArgs
{
    internal ErasureCodedVolumeStateChangedEventArgs(
        ErasureCodedVolumeState previous,
        ErasureCodedVolumeState current)
    {
        Previous = previous;
        Current = current;
    }

    /// <summary>Gets the snapshot before the transition.</summary>
    public ErasureCodedVolumeState Previous { get; }

    /// <summary>Gets the snapshot after the transition.</summary>
    public ErasureCodedVolumeState Current { get; }
}
