namespace TeeForge.Broadcasting;

/// <summary>Configures shared buffering and destination failures for a broadcast copy.</summary>
public class BroadcastCopyOptions
{
    /// <summary>Gets the default copy options.</summary>
    public static BroadcastCopyOptions Default { get; } = new();

    /// <summary>Initializes immutable broadcast-copy options.</summary>
    /// <param name="bufferSize">The maximum source-read and destination-write size.</param>
    /// <param name="pauseWriterThreshold">Unread bytes at the slowest destination that pause the source pump.</param>
    /// <param name="resumeWriterThreshold">Unread bytes below which every active destination must advance to resume.</param>
    /// <param name="failureBehavior">Whether a failed destination stops the copy or permits healthy copies to continue.</param>
    public BroadcastCopyOptions(
        int bufferSize = 4096,
        long pauseWriterThreshold = 65536,
        long resumeWriterThreshold = 32768,
        BroadcastCopyFailureBehavior failureBehavior = BroadcastCopyFailureBehavior.Stop)
    {
        if (!Enum.IsDefined(failureBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(failureBehavior));
        }

        StreamOptions = new BroadcastStreamOptions(bufferSize, pauseWriterThreshold, resumeWriterThreshold, leaveOpen: true);
        FailureBehavior = failureBehavior;
    }

    /// <summary>Gets the maximum source-read and destination-write size.</summary>
    public int BufferSize => StreamOptions.BufferSize;

    /// <summary>Gets the shared-buffer pause threshold.</summary>
    public long PauseWriterThreshold => StreamOptions.PauseWriterThreshold;

    /// <summary>Gets the shared-buffer resume threshold.</summary>
    public long ResumeWriterThreshold => StreamOptions.ResumeWriterThreshold;

    /// <summary>Gets the destination failure policy.</summary>
    public BroadcastCopyFailureBehavior FailureBehavior { get; }

    internal BroadcastStreamOptions StreamOptions { get; }
}
