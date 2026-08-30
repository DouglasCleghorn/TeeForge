namespace TeeForge.Mirroring;

/// <summary>Provides immutable options for a <see cref="ReplicaStream"/>.</summary>
public class ReplicaStreamOptions
{
    /// <summary>Gets the default replica-stream options.</summary>
    public static ReplicaStreamOptions Default { get; } = new();

    /// <summary>Initializes a new options instance.</summary>
    /// <param name="synchronousMode">How independent synchronous calls are dispatched.</param>
    /// <param name="leaveOpen">Whether disposing the wrapper leaves every replica open.</param>
    public ReplicaStreamOptions(
        TeeStreamSynchronousMode synchronousMode = TeeStreamSynchronousMode.Sequential,
        bool leaveOpen = false)
    {
        if (!Enum.IsDefined(synchronousMode))
        {
            throw new ArgumentOutOfRangeException(nameof(synchronousMode));
        }

        SynchronousMode = synchronousMode;
        LeaveOpen = leaveOpen;
    }

    /// <summary>Gets how independent synchronous calls are dispatched.</summary>
    public TeeStreamSynchronousMode SynchronousMode { get; }

    /// <summary>Gets whether disposing the wrapper leaves every replica open.</summary>
    public bool LeaveOpen { get; }
}
