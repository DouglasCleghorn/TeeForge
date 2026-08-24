namespace TeeForge.Pipelines;

/// <summary>Specifies how a <see cref="TeePipe"/> reacts when one reader completes with an exception.</summary>
public enum TeePipeReaderFailureBehavior
{
    /// <summary>Removes the failed reader and continues serving healthy readers.</summary>
    Continue = 0,

    /// <summary>Faults the writer and lets healthy readers drain already-flushed data before observing the exception.</summary>
    CompletePipe = 1,
}
