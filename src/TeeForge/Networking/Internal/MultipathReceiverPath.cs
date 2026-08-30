namespace TeeForge.Networking.Internal;

internal sealed class MultipathReceiverPath : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly CancellationTokenSource _stopSource = new();
    private readonly CancellationToken _stopToken;
    private int _disposeState;

    internal MultipathReceiverPath(Guid pathId, Stream stream, bool leaveOpen)
    {
        PathId = pathId;
        _stream = stream;
        _leaveOpen = leaveOpen;
        _stopToken = _stopSource.Token;
    }

    internal Guid PathId { get; }

    internal CancellationToken StopToken => _stopToken;

    internal Stream Stream => _stream;

    internal ValueTask StopAsync() => DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        await _stopSource.CancelAsync().ConfigureAwait(false);
        if (!_leaveOpen)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        _stopSource.Dispose();
    }
}
