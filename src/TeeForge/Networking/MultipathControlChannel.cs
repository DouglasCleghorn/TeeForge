using TeeForge.Networking.Internal;

namespace TeeForge.Networking;

/// <summary>
/// Sends and receives optional path-health, mode-request, and endpoint-advertisement messages.
/// </summary>
/// <remarks>One send and one receive may run concurrently. Calls in the same direction are serialized.</remarks>
public class MultipathControlChannel : IAsyncDisposable, IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _disposeState;

    /// <summary>Initializes a control channel over a reliable ordered stream.</summary>
    public MultipathControlChannel(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead && !stream.CanWrite)
        {
            throw new ArgumentException("A control stream must be readable, writable, or both.", nameof(stream));
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    /// <summary>Gets whether this endpoint can receive control messages.</summary>
    public bool CanReceive => Volatile.Read(ref _disposeState) == 0 && _stream.CanRead;

    /// <summary>Gets whether this endpoint can send control messages.</summary>
    public bool CanSend => Volatile.Read(ref _disposeState) == 0 && _stream.CanWrite;

    /// <summary>Sends one complete control message.</summary>
    public async ValueTask SendAsync(
        MultipathControlMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        if (!_stream.CanWrite)
        {
            throw new NotSupportedException("This control endpoint is not writable.");
        }

        byte[] frame = MultipathControlProtocol.Encode(message);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Receives one control message, or <see langword="null"/> at a clean end of stream.</summary>
    public async ValueTask<MultipathControlMessage?> ReceiveAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_stream.CanRead)
        {
            throw new NotSupportedException("This control endpoint is not readable.");
        }

        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await MultipathControlProtocol.ReadAsync(_stream, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _readGate.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }

            _readGate.Dispose();
            _writeGate.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            if (!_leaveOpen)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }

            _readGate.Dispose();
            _writeGate.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
}
