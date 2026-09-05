namespace TeeForge.Experimental.Storage.ErasureCoding;

/// <summary>Controls how aggressively a maintenance operation competes with foreground I/O.</summary>
public enum ErasureMaintenancePriority
{
    /// <summary>Runs continuously, subject only to an optional bandwidth limit.</summary>
    Foreground,

    /// <summary>Yields between stripes so waiting foreground operations can proceed.</summary>
    Balanced,

    /// <summary>Yields and pauses between stripes to reduce foreground interference.</summary>
    Background,
}

/// <summary>Identifies a maintenance operation.</summary>
public enum ErasureMaintenanceOperation
{
    /// <summary>Validates every current shard header and integrity block.</summary>
    ConsistencyCheck,
}

/// <summary>Describes the lifecycle state of a maintenance operation.</summary>
public enum ErasureMaintenanceStatus
{
    /// <summary>The operation is running.</summary>
    Running,

    /// <summary>The operation completed.</summary>
    Completed,

    /// <summary>The operation was canceled.</summary>
    Canceled,

    /// <summary>The operation failed.</summary>
    Faulted,
}

/// <summary>Configures maintenance scheduling and bandwidth.</summary>
public sealed class ErasureMaintenanceOptions
{
    /// <summary>Gets default background maintenance settings.</summary>
    public static ErasureMaintenanceOptions Default { get; } = new();

    /// <summary>Initializes maintenance options.</summary>
    /// <param name="priority">How aggressively maintenance competes with foreground operations.</param>
    /// <param name="maximumBytesPerSecond">Maximum validated bytes per second; zero means unlimited.</param>
    /// <param name="backgroundDelay">Additional delay after each stripe in background mode.</param>
    public ErasureMaintenanceOptions(
        ErasureMaintenancePriority priority = ErasureMaintenancePriority.Background,
        long maximumBytesPerSecond = 0,
        TimeSpan? backgroundDelay = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytesPerSecond);
        TimeSpan delay = backgroundDelay ?? TimeSpan.FromMilliseconds(25);
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(backgroundDelay));
        }

        Priority = priority;
        MaximumBytesPerSecond = maximumBytesPerSecond;
        BackgroundDelay = delay;
    }

    /// <summary>Gets the scheduling priority.</summary>
    public ErasureMaintenancePriority Priority { get; }

    /// <summary>Gets the byte-rate ceiling, or zero for unlimited.</summary>
    public long MaximumBytesPerSecond { get; }

    /// <summary>Gets the delay inserted after each stripe in background mode.</summary>
    public TimeSpan BackgroundDelay { get; }
}

/// <summary>Reports point-in-time progress for a maintenance operation.</summary>
public sealed class ErasureMaintenanceProgress
{
    internal ErasureMaintenanceProgress(
        Guid operationId,
        ErasureMaintenanceOperation operation,
        ErasureMaintenanceStatus status,
        long completedBytes,
        long totalBytes,
        int inconsistentMembers,
        Exception? error)
    {
        OperationId = operationId;
        Operation = operation;
        Status = status;
        CompletedBytes = completedBytes;
        TotalBytes = totalBytes;
        InconsistentMembers = inconsistentMembers;
        Error = error;
    }

    /// <summary>Gets the unique invocation identifier.</summary>
    public Guid OperationId { get; }

    /// <summary>Gets the kind of maintenance operation.</summary>
    public ErasureMaintenanceOperation Operation { get; }

    /// <summary>Gets the lifecycle status.</summary>
    public ErasureMaintenanceStatus Status { get; }

    /// <summary>Gets the member-byte range examined, including unavailable ranges.</summary>
    public long CompletedBytes { get; }

    /// <summary>Gets total bytes scheduled for validation.</summary>
    public long TotalBytes { get; }

    /// <summary>Gets the number of distinct unavailable, stale, or corrupt members observed so far.</summary>
    public int InconsistentMembers { get; }

    /// <summary>Gets the failure when <see cref="Status"/> is <see cref="ErasureMaintenanceStatus.Faulted"/>.</summary>
    public Exception? Error { get; }
}

/// <summary>Contains the result of a complete consistency check.</summary>
public sealed class ErasureConsistencyCheckResult
{
    internal ErasureConsistencyCheckResult(
        Guid operationId,
        long checkedBytes,
        long checkedStripes,
        IReadOnlyList<int> inconsistentMemberPositions)
    {
        OperationId = operationId;
        CheckedBytes = checkedBytes;
        CheckedStripes = checkedStripes;
        InconsistentMemberPositions = inconsistentMemberPositions;
    }

    /// <summary>Gets the maintenance invocation identifier.</summary>
    public Guid OperationId { get; }

    /// <summary>Gets the member-byte range examined, including unavailable ranges.</summary>
    public long CheckedBytes { get; }

    /// <summary>Gets validated logical stripes.</summary>
    public long CheckedStripes { get; }

    /// <summary>Gets distinct positions that were missing, stale, or corrupt.</summary>
    public IReadOnlyList<int> InconsistentMemberPositions { get; }

    /// <summary>Gets whether all configured members contained valid current data.</summary>
    public bool IsConsistent => InconsistentMemberPositions.Count == 0;
}
