namespace TeeForge.Broadcasting.Internal;

internal interface IBroadcastObserver : IDisposable
{
    void Append(ReadOnlySpan<byte> bytes);

    void Complete();
}
