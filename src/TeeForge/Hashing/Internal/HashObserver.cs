using TeeForge.Broadcasting.Internal;

namespace TeeForge.Hashing.Internal;

internal sealed class HashObserver(TeeHashAlgorithmId[] algorithms, TeeHashResults results) : IBroadcastObserver
{
    private readonly HashWriteStream[] _streams = HashCompletionCoordinator.CreateStreams(algorithms, results);

    public void Append(ReadOnlySpan<byte> bytes)
    {
        foreach (HashWriteStream stream in _streams)
        {
            stream.Write(bytes);
        }
    }

    public void Complete()
    {
        foreach (HashWriteStream stream in _streams)
        {
            stream.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (HashWriteStream stream in _streams)
        {
            stream.DisposeWithoutFinalizing();
        }
    }
}
