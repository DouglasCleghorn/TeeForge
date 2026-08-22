namespace TeeForge;

/// <summary>Describes one destination whose successful result differed from the primary destination.</summary>
/// <param name="DestinationIndex">The zero-based destination index.</param>
/// <param name="DestinationResult">The numeric result, when the operation has one.</param>
/// <param name="FirstDifferingByteOffset">The first differing read offset, when read data differed.</param>
public readonly record struct TeeStreamMismatch(
    int DestinationIndex,
    long? DestinationResult,
    long? FirstDifferingByteOffset);
