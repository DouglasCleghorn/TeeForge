using System.Collections.ObjectModel;

namespace TeeForge.Mirroring;

/// <summary>The exception thrown when successful destination results do not agree.</summary>
public class TeeStreamConsistencyException : IOException
{
    /// <summary>Initializes a consistency exception.</summary>
    /// <param name="operationName">The Stream operation that produced inconsistent results.</param>
    /// <param name="primaryResult">The primary numeric result, when the operation has one.</param>
    /// <param name="mismatches">The destination mismatch metadata.</param>
    public TeeStreamConsistencyException(
        string operationName,
        long? primaryResult,
        IEnumerable<TeeStreamMismatch> mismatches)
        : this(operationName, primaryResult, CopyMismatches(mismatches))
    {
    }

    private TeeStreamConsistencyException(
        string operationName,
        long? primaryResult,
        ReadOnlyCollection<TeeStreamMismatch> mismatches)
        : base(CreateMessage(operationName, mismatches))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        OperationName = operationName;
        PrimaryResult = primaryResult;
        Mismatches = mismatches;
    }

    /// <summary>Gets the Stream operation that produced inconsistent results.</summary>
    public string OperationName { get; }

    /// <summary>Gets the primary numeric result, when the operation has one.</summary>
    public long? PrimaryResult { get; }

    /// <summary>Gets immutable metadata for destinations that differed.</summary>
    public IReadOnlyList<TeeStreamMismatch> Mismatches { get; }

    private static ReadOnlyCollection<TeeStreamMismatch> CopyMismatches(IEnumerable<TeeStreamMismatch> mismatches)
    {
        ArgumentNullException.ThrowIfNull(mismatches);
        TeeStreamMismatch[] copy = mismatches.ToArray();
        if (copy.Length == 0)
        {
            throw new ArgumentException("At least one mismatch is required.", nameof(mismatches));
        }

        return Array.AsReadOnly(copy);
    }

    private static string CreateMessage(string operationName, ReadOnlyCollection<TeeStreamMismatch> mismatches)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        return $"TeeStream operation '{operationName}' produced inconsistent results for {mismatches.Count} destination(s).";
    }
}
