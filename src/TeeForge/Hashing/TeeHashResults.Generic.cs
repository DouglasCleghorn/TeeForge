using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace TeeForge.Hashing;

/// <summary>Exposes an empty read-only dictionary until every configured hash is atomically published.</summary>
/// <typeparam name="TAlgorithm">The enum type identifying the hash algorithms.</typeparam>
[SuppressMessage(
    "Naming",
    "CA1710:Identifiers should have correct suffix",
    Justification = "TeeHashResults is the accepted domain name for the completed hash collection.")]
public class TeeHashResults<TAlgorithm> : IReadOnlyDictionary<TAlgorithm, TeeHashResult<TAlgorithm>>
    where TAlgorithm : struct, Enum
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
    public IEnumerable<TAlgorithm> Keys => Volatile.Read(ref _snapshot).Values.Keys;

    /// <inheritdoc/>
    public IEnumerable<TeeHashResult<TAlgorithm>> Values => Volatile.Read(ref _snapshot).Values.Values;

    /// <inheritdoc/>
    public TeeHashResult<TAlgorithm> this[TAlgorithm key] => Volatile.Read(ref _snapshot).Values[key];

    /// <inheritdoc/>
    public bool ContainsKey(TAlgorithm key) => Volatile.Read(ref _snapshot).Values.ContainsKey(key);

    /// <inheritdoc/>
    public bool TryGetValue(TAlgorithm key, out TeeHashResult<TAlgorithm> value) =>
        Volatile.Read(ref _snapshot).Values.TryGetValue(key, out value!);

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<TAlgorithm, TeeHashResult<TAlgorithm>>> GetEnumerator() =>
        Volatile.Read(ref _snapshot).Values.GetEnumerator();

    /// <inheritdoc/>
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    internal void Publish(IEnumerable<TeeHashResult<TAlgorithm>> completedResults)
    {
        var values = new OrderedDictionary<TAlgorithm, TeeHashResult<TAlgorithm>>();
        foreach (TeeHashResult<TAlgorithm> result in completedResults)
        {
            values.Add(result.Algorithm, result);
        }

        var completed = new Snapshot(
            isComplete: true,
            new ReadOnlyDictionary<TAlgorithm, TeeHashResult<TAlgorithm>>(values));

        if (!ReferenceEquals(
                Interlocked.CompareExchange(ref _snapshot, completed, Snapshot.Incomplete),
                Snapshot.Incomplete))
        {
            throw new InvalidOperationException("Hash results have already been published.");
        }
    }

    private sealed class Snapshot(
        bool isComplete,
        IReadOnlyDictionary<TAlgorithm, TeeHashResult<TAlgorithm>> values)
    {
        internal static Snapshot Incomplete { get; } = new(
            isComplete: false,
            new ReadOnlyDictionary<TAlgorithm, TeeHashResult<TAlgorithm>>(
                new OrderedDictionary<TAlgorithm, TeeHashResult<TAlgorithm>>()));

        internal bool IsComplete { get; } = isComplete;

        internal IReadOnlyDictionary<TAlgorithm, TeeHashResult<TAlgorithm>> Values { get; } = values;
    }
}
