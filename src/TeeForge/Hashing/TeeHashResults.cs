using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace TeeForge.Hashing;

/// <summary>
/// Exposes an empty read-only dictionary until every configured hash is atomically published.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1710:Identifiers should have correct suffix",
    Justification = "TeeHashResults is the accepted domain name for the completed hash collection.")]
public class TeeHashResults : IReadOnlyDictionary<TeeHashAlgorithmId, TeeHashResult>
{
    private Snapshot _snapshot = Snapshot.Incomplete;

    internal TeeHashResults()
    {
    }

    /// <summary>Gets whether every configured hash has been finalized and published.</summary>
    public bool IsComplete => Volatile.Read(ref _snapshot).IsComplete;

    /// <inheritdoc/>
    public int Count => Volatile.Read(ref _snapshot).Values.Count;

    /// <inheritdoc/>
    public IEnumerable<TeeHashAlgorithmId> Keys => Volatile.Read(ref _snapshot).Values.Keys;

    /// <inheritdoc/>
    public IEnumerable<TeeHashResult> Values => Volatile.Read(ref _snapshot).Values.Values;

    /// <inheritdoc/>
    public TeeHashResult this[TeeHashAlgorithmId key] => Volatile.Read(ref _snapshot).Values[key];

    /// <inheritdoc/>
    public bool ContainsKey(TeeHashAlgorithmId key) => Volatile.Read(ref _snapshot).Values.ContainsKey(key);

    /// <inheritdoc/>
    public bool TryGetValue(TeeHashAlgorithmId key, out TeeHashResult value) =>
        Volatile.Read(ref _snapshot).Values.TryGetValue(key, out value!);

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<TeeHashAlgorithmId, TeeHashResult>> GetEnumerator() =>
        Volatile.Read(ref _snapshot).Values.GetEnumerator();

    /// <inheritdoc/>
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    internal void Publish(IEnumerable<TeeHashResult> completedResults)
    {
        var values = new OrderedDictionary<TeeHashAlgorithmId, TeeHashResult>();
        foreach (TeeHashResult result in completedResults)
        {
            values.Add(result.Algorithm, result);
        }

        var completed = new Snapshot(
            isComplete: true,
            new ReadOnlyDictionary<TeeHashAlgorithmId, TeeHashResult>(values));

        if (!ReferenceEquals(
                Interlocked.CompareExchange(ref _snapshot, completed, Snapshot.Incomplete),
                Snapshot.Incomplete))
        {
            throw new InvalidOperationException("Hash results have already been published.");
        }
    }

    private sealed class Snapshot(
        bool isComplete,
        IReadOnlyDictionary<TeeHashAlgorithmId, TeeHashResult> values)
    {
        internal static Snapshot Incomplete { get; } = new(
            isComplete: false,
            new ReadOnlyDictionary<TeeHashAlgorithmId, TeeHashResult>(
                new OrderedDictionary<TeeHashAlgorithmId, TeeHashResult>()));

        internal bool IsComplete { get; } = isComplete;

        internal IReadOnlyDictionary<TeeHashAlgorithmId, TeeHashResult> Values { get; } = values;
    }
}
