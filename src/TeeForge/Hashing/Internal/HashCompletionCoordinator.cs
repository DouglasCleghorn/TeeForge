namespace TeeForge.Hashing.Internal;

internal sealed class HashCompletionCoordinator
{
    private readonly TeeHashAlgorithmId[] _algorithms;
    private readonly byte[]?[] _digests;
    private readonly TeeHashResults _results;
    private int _remaining;

    private HashCompletionCoordinator(TeeHashAlgorithmId[] algorithms, TeeHashResults results)
    {
        _algorithms = algorithms;
        _digests = new byte[algorithms.Length][];
        _remaining = algorithms.Length;
        _results = results;
    }

    internal static HashWriteStream[] CreateStreams(TeeHashAlgorithmId[] algorithms, TeeHashResults results)
    {
        var completion = new HashCompletionCoordinator(algorithms, results);
        var streams = new HashWriteStream[algorithms.Length];
        try
        {
            for (int index = 0; index < streams.Length; index++)
            {
                streams[index] = new HashWriteStream(algorithms[index], completion, index);
            }

            return streams;
        }
        catch
        {
            foreach (HashWriteStream? stream in streams)
            {
                stream?.DisposeWithoutFinalizing();
            }

            throw;
        }
    }

    internal void Complete(int index, byte[] digest)
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

        var completed = new TeeHashResult[_algorithms.Length];
        for (int resultIndex = 0; resultIndex < completed.Length; resultIndex++)
        {
            completed[resultIndex] = TeeHashResult.FromOwnedBytes(
                _algorithms[resultIndex],
                _digests[resultIndex]!);
        }

        _results.Publish(completed);
    }
}
