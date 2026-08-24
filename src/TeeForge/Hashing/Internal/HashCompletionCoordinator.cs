namespace TeeForge.Hashing.Internal;

internal interface IHashCompletionCoordinator
{
    void Complete(int index, byte[] digest);
}

internal sealed class HashCompletionCoordinator<TAlgorithm, TResult> : IHashCompletionCoordinator
    where TAlgorithm : notnull
{
    private readonly TAlgorithm[] _algorithms;
    private readonly byte[]?[] _digests;
    private readonly Action<IEnumerable<TResult>> _publish;
    private readonly Func<TAlgorithm, byte[], TResult> _resultFactory;
    private int _remaining;

    internal HashCompletionCoordinator(
        TAlgorithm[] algorithms,
        Func<TAlgorithm, byte[], TResult> resultFactory,
        Action<IEnumerable<TResult>> publish)
    {
        _algorithms = algorithms;
        _digests = new byte[algorithms.Length][];
        _remaining = algorithms.Length;
        _resultFactory = resultFactory;
        _publish = publish;
    }

    public void Complete(int index, byte[] digest)
    {
        ArgumentNullException.ThrowIfNull(digest);

        if (Interlocked.CompareExchange(ref _digests[index], digest, null) is not null)
        {
            throw new InvalidOperationException($"Hash result at index {index} was completed more than once.");
        }

        if (Interlocked.Decrement(ref _remaining) != 0)
        {
            return;
        }

        var completed = new TResult[_algorithms.Length];
        for (int resultIndex = 0; resultIndex < completed.Length; resultIndex++)
        {
            completed[resultIndex] = _resultFactory(
                _algorithms[resultIndex],
                _digests[resultIndex]!);
        }

        _publish(completed);
    }
}
