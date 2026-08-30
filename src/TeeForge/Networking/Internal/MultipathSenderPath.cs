namespace TeeForge.Networking.Internal;

internal sealed class MultipathSenderPath : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _queueSlots;
    private int _disposeState;

    internal MultipathSenderPath(Guid pathId, Stream stream, int queueCapacity, bool leaveOpen)
    {
        PathId = pathId;
        _stream = stream;
        _queueSlots = new SemaphoreSlim(queueCapacity, queueCapacity);
        _leaveOpen = leaveOpen;
    }

    internal Guid PathId { get; }

    internal async Task SendAsync(byte[] frame, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        if (!_queueSlots.Wait(0, cancellationToken))
        {
            throw new IOException("The multipath data path exceeded its bounded send queue.");
        }

        try
        {
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
                await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendGate.Release();
            }
        }
        finally
        {
            _queueSlots.Release();
        }
    }

    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0 && !_leaveOpen)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

    }
}
