namespace TeeForge.ErasureCoding;

/// <summary>Provides immutable creation, opening, telemetry, and ownership options for an erasure-coded stream.</summary>
public class ErasureCodeStreamOptions
{
    /// <summary>Gets the default options.</summary>
    public static ErasureCodeStreamOptions Default { get; } = new();

    /// <summary>Initializes a new options instance.</summary>
    /// <param name="leaveOpen">Whether disposing the erasure stream leaves supplied member streams open.</param>
    /// <param name="readOnly">Whether opening forces read-only operation.</param>
    /// <param name="journalSlotCount">Number of bounded journal slots created per member.</param>
    /// <param name="latencySampleRate">One operation in this many is latency sampled; zero disables successful-operation sampling.</param>
    public ErasureCodeStreamOptions(
        bool leaveOpen = false,
        bool readOnly = false,
        int journalSlotCount = 4,
        int latencySampleRate = 64)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            journalSlotCount,
            Internal.ErasureFormatV1.MinimumJournalSlotCount);
        ArgumentOutOfRangeException.ThrowIfNegative(latencySampleRate);

        LeaveOpen = leaveOpen;
        ReadOnly = readOnly;
        JournalSlotCount = journalSlotCount;
        LatencySampleRate = latencySampleRate;
    }

    /// <summary>Gets whether supplied member streams remain open after disposal.</summary>
    public bool LeaveOpen { get; }

    /// <summary>Gets whether opening forces read-only operation.</summary>
    public bool ReadOnly { get; }

    /// <summary>Gets the number of journal slots created per member.</summary>
    public int JournalSlotCount { get; }

    /// <summary>Gets the deterministic successful-operation latency sampling interval.</summary>
    public int LatencySampleRate { get; }
}
