using System.IO.Compression;
using System.IO.Pipelines;
using System.Net.Quic;
using System.Runtime.Versioning;

namespace TeeForge.Networking;

/// <summary>Provides one dynamically named, full-duplex application stream over QUIC.</summary>
/// <remarks>
/// One read and one write may run concurrently. Same-direction calls are serialized, including
/// calls made through <see cref="Input"/> and <see cref="Output"/>. Select either the
/// <see cref="Stream"/> API or the pipe API for each direction because both surfaces share the
/// same ordered byte sequence.
/// </remarks>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class NamedQuicStream : Stream, IDuplexPipe
{
    private readonly QuicStream _transport;
    private readonly Stream _readPayload;
    private readonly Stream _writePayload;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Action _releaseName;
    private int _writeCompletionState;
    private int _disposeState;

    internal NamedQuicStream(
        string name,
        QuicStream transport,
        QuicStreamCompression compression,
        Action releaseName)
    {
        Name = name;
        _transport = transport;
        Compression = compression;
        _releaseName = releaseName;
        if (compression == QuicStreamCompression.None)
        {
            _readPayload = transport;
            _writePayload = transport;
        }
        else
        {
            _readPayload = new BrotliStream(transport, CompressionMode.Decompress, leaveOpen: true);
            _writePayload = new BrotliStream(
                transport,
                QuicProtocol.GetCompressionLevel(compression),
                leaveOpen: true);
        }

        Input = PipeReader.Create(this, new StreamPipeReaderOptions(leaveOpen: true));
        Output = PipeWriter.Create(this, new StreamPipeWriterOptions(leaveOpen: true));
    }

    /// <summary>Gets the dynamic application name negotiated in the opening preface.</summary>
    public string Name { get; }

    /// <summary>Gets the QUIC-assigned physical stream identifier.</summary>
    public long Id => _transport.Id;

    /// <summary>Gets the transparent payload compression negotiated for this stream.</summary>
    public QuicStreamCompression Compression { get; }

    /// <summary>Gets the pipe reader for received payload bytes.</summary>
    public PipeReader Input { get; }

    /// <summary>Gets the pipe writer for transmitted payload bytes.</summary>
    public PipeWriter Output { get; }

    /// <summary>Gets a task that completes when the read side closes.</summary>
    public Task ReadsClosed => _transport.ReadsClosed;

    /// <summary>Gets a task that completes when the write side closes.</summary>
    public Task WritesClosed => _transport.WritesClosed;

    /// <inheritdoc/>
    public override bool CanRead => Volatile.Read(ref _disposeState) == 0 && _readPayload.CanRead;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite =>
        Volatile.Read(ref _disposeState) == 0 &&
        Volatile.Read(ref _writeCompletionState) == 0 &&
        _writePayload.CanWrite;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Gracefully finalizes payload compression and closes the write direction.</summary>
    public void CompleteWrites()
    {
        ThrowIfDisposed();
        _writeGate.Wait();
        try
        {
            ThrowIfDisposed();
            CompleteWritesCore();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Aborts one or both stream directions with an application error code.</summary>
    public void Abort(QuicAbortDirection direction, long errorCode)
    {
        ThrowIfDisposed();
        _transport.Abort(direction, errorCode);
    }

    /// <inheritdoc/>
    public override void Flush()
    {
        ThrowIfDisposed();
        _writeGate.Wait();
        try
        {
            ThrowIfDisposed();
            ThrowIfWritesCompleted();
            _writePayload.Flush();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc/>
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ThrowIfWritesCompleted();
            await _writePayload.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        _readGate.Wait();
        try
        {
            ThrowIfDisposed();
            return _readPayload.Read(buffer);
        }
        finally
        {
            _readGate.Release();
        }
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await _readPayload.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _readGate.Release();
        }
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfDisposed();
        _writeGate.Wait();
        try
        {
            ThrowIfDisposed();
            ThrowIfWritesCompleted();
            _writePayload.Write(buffer);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc/>
    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc/>
    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ThrowIfWritesCompleted();
            await _writePayload.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            try
            {
                _writeGate.Wait();
                try
                {
                    CompleteWritesCore();
                }
                finally
                {
                    _writeGate.Release();
                }

                if (!ReferenceEquals(_readPayload, _transport))
                {
                    _readPayload.Dispose();
                }

                _transport.Dispose();
            }
            finally
            {
                _releaseName();
            }
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            try
            {
                await _writeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await CompleteWritesCoreAsync().ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }

                if (!ReferenceEquals(_readPayload, _transport))
                {
                    await _readPayload.DisposeAsync().ConfigureAwait(false);
                }

                await _transport.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _releaseName();
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void CompleteWritesCore()
    {
        if (Interlocked.Exchange(ref _writeCompletionState, 1) != 0)
        {
            return;
        }

        if (!ReferenceEquals(_writePayload, _transport))
        {
            _writePayload.Dispose();
        }

        _transport.CompleteWrites();
    }

    private async ValueTask CompleteWritesCoreAsync()
    {
        if (Interlocked.Exchange(ref _writeCompletionState, 1) != 0)
        {
            return;
        }

        if (!ReferenceEquals(_writePayload, _transport))
        {
            await _writePayload.DisposeAsync().ConfigureAwait(false);
        }

        _transport.CompleteWrites();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);

    private void ThrowIfWritesCompleted()
    {
        if (Volatile.Read(ref _writeCompletionState) != 0)
        {
            throw new InvalidOperationException("The named QUIC stream's write direction is complete.");
        }
    }
}
