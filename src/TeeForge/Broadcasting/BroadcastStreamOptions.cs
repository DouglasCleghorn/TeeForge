namespace TeeForge.Broadcasting;

/// <summary>Configures the shared buffer and source ownership of a broadcast.</summary>
public class BroadcastStreamOptions
{
    /// <summary>Gets the default options.</summary>
    public static BroadcastStreamOptions Default { get; } = new();

    /// <summary>Initializes immutable broadcast options.</summary>
    /// <param name="bufferSize">The maximum bytes requested in each source read.</param>
    /// <param name="pauseWriterThreshold">Unread bytes at the slowest reader that pause the source pump.</param>
    /// <param name="resumeWriterThreshold">Unread bytes below which every reader must advance to resume the pump.</param>
    /// <param name="leaveOpen">Whether disposing the broadcast leaves its source open.</param>
    public BroadcastStreamOptions(
        int bufferSize = 4096,
        long pauseWriterThreshold = 65536,
        long resumeWriterThreshold = 32768,
        bool leaveOpen = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pauseWriterThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resumeWriterThreshold);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(resumeWriterThreshold, pauseWriterThreshold);

        BufferSize = bufferSize;
        PauseWriterThreshold = pauseWriterThreshold;
        ResumeWriterThreshold = resumeWriterThreshold;
        LeaveOpen = leaveOpen;
    }

    /// <summary>Gets the maximum source-read size and minimum pooled segment size.</summary>
    public int BufferSize { get; }

    /// <summary>Gets the pause threshold, which may be exceeded by less than one source-read buffer.</summary>
    public long PauseWriterThreshold { get; }

    /// <summary>Gets the resume threshold for the slowest active reader.</summary>
    public long ResumeWriterThreshold { get; }

    /// <summary>Gets whether the caller retains ownership of the source.</summary>
    public bool LeaveOpen { get; }
}
