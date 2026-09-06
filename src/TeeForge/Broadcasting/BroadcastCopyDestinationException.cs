namespace TeeForge.Broadcasting;

/// <summary>Identifies a failed destination within a broadcast copy's AggregateException.</summary>
public class BroadcastCopyDestinationException : IOException
{
    /// <summary>Initializes a destination failure while retaining its original exception.</summary>
    /// <param name="destinationIndex">The zero-based index in the supplied destination collection.</param>
    /// <param name="innerException">The exception thrown by that destination.</param>
    public BroadcastCopyDestinationException(int destinationIndex, Exception innerException)
        : base($"Broadcast copy destination {destinationIndex} failed.", innerException)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(destinationIndex);
        ArgumentNullException.ThrowIfNull(innerException);
        DestinationIndex = destinationIndex;
    }

    /// <summary>Gets the zero-based index in the supplied destination collection.</summary>
    public int DestinationIndex { get; }
}
