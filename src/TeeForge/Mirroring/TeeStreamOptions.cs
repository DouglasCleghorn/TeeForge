namespace TeeForge.Mirroring;

/// <summary>Provides immutable options for a <see cref="TeeStream"/>.</summary>
public class TeeStreamOptions
{
    /// <summary>Gets the default options.</summary>
    public static TeeStreamOptions Default { get; } = new();

    /// <summary>Initializes a new options instance.</summary>
    /// <param name="mismatchBehavior">How successful differences are handled.</param>
    /// <param name="synchronousMode">How independent synchronous calls are dispatched.</param>
    /// <param name="leaveOpen">Whether disposing the wrapper leaves every destination open.</param>
    public TeeStreamOptions(
        TeeStreamMismatchBehavior mismatchBehavior = TeeStreamMismatchBehavior.ThrowAndContinue,
        TeeStreamSynchronousMode synchronousMode = TeeStreamSynchronousMode.Sequential,
        bool leaveOpen = false)
    {
        if (!Enum.IsDefined(mismatchBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(mismatchBehavior));
        }

        if (!Enum.IsDefined(synchronousMode))
        {
            throw new ArgumentOutOfRangeException(nameof(synchronousMode));
        }

        MismatchBehavior = mismatchBehavior;
        SynchronousMode = synchronousMode;
        LeaveOpen = leaveOpen;
    }

    /// <summary>Gets how successful differences are handled.</summary>
    public TeeStreamMismatchBehavior MismatchBehavior { get; }

    /// <summary>Gets how independent synchronous calls are dispatched.</summary>
    public TeeStreamSynchronousMode SynchronousMode { get; }

    /// <summary>Gets whether disposing the wrapper leaves every destination open.</summary>
    public bool LeaveOpen { get; }
}
