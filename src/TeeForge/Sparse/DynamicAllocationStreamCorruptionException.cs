namespace TeeForge.Sparse;

/// <summary>Represents structurally invalid dynamic-allocation metadata.</summary>
public class DynamicAllocationStreamCorruptionException : IOException
{
    /// <summary>Initializes a corruption exception.</summary>
    /// <param name="message">A description of the invalid structure.</param>
    /// <param name="physicalOffset">The failing physical offset, when known.</param>
    public DynamicAllocationStreamCorruptionException(string message, long? physicalOffset = null)
        : base(FormatMessage(message, physicalOffset))
    {
        PhysicalOffset = physicalOffset;
    }

    /// <summary>Initializes a corruption exception with an inner exception.</summary>
    /// <param name="message">A description of the invalid structure.</param>
    /// <param name="physicalOffset">The failing physical offset, when known.</param>
    /// <param name="innerException">The underlying failure.</param>
    public DynamicAllocationStreamCorruptionException(
        string message,
        long? physicalOffset,
        Exception innerException)
        : base(FormatMessage(message, physicalOffset), innerException)
    {
        PhysicalOffset = physicalOffset;
    }

    /// <summary>Gets the physical offset associated with the corruption, when known.</summary>
    public long? PhysicalOffset { get; }

    private static string FormatMessage(string message, long? physicalOffset) =>
        physicalOffset is null ? message : $"{message} (physical offset {physicalOffset.Value}).";
}
