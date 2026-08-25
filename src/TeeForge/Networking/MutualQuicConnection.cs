using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using TeeForge.RandomAccess;

namespace TeeForge.Networking;

/// <summary>Owns one mutually authenticated QUIC connection and routes its application streams.</summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class MutualQuicConnection : IAsyncDisposable
{
    private const int RandomAccessRequestHeaderSize = 22;
    private const int RandomAccessResponseSize = 10;
    private readonly QuicConnection _transport;
    private readonly MutualQuicConnectionOptions _options;
    private readonly bool _isClient;
    private readonly X509Certificate2? _ownedLocalCertificate;
    private readonly X509Certificate2? _ownedTrustedPeerCertificate;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Channel<NamedQuicStream> _acceptedNamedStreams;
    private readonly object _namedSync = new();
    private readonly Dictionary<string, NamedReservation> _namedStreams =
        new(StringComparer.Ordinal);
    private readonly object _randomAccessSync = new();
    private readonly Dictionary<string, ITeeRandomAccessStream> _localRandomAccess =
        new(StringComparer.Ordinal);
    private readonly Dictionary<uint, RandomAccessSession> _randomAccessSessions = [];
    private readonly object _inboundTasksSync = new();
    private readonly HashSet<Task> _inboundTasks = [];
    private readonly Task _inboundPump;
    private uint _nextRandomAccessHandle;
    private int _disposeState;

    internal MutualQuicConnection(
        QuicConnection transport,
        MutualQuicConnectionOptions options,
        bool isClient,
        X509Certificate2? ownedLocalCertificate = null,
        X509Certificate2? ownedTrustedPeerCertificate = null)
    {
        _transport = transport;
        _options = options;
        _isClient = isClient;
        _ownedLocalCertificate = ownedLocalCertificate;
        _ownedTrustedPeerCertificate = ownedTrustedPeerCertificate;
        _acceptedNamedStreams = Channel.CreateBounded<NamedQuicStream>(
            new BoundedChannelOptions(options.MaximumPendingNamedStreams)
            {
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
            });
        _inboundPump = RunInboundPumpAsync(_lifetimeCancellation.Token);
    }

    /// <summary>Gets whether the current platform supports QUIC clients.</summary>
    public static bool IsSupported => QuicConnection.IsSupported;

    /// <summary>Gets whether this endpoint initiated the connection.</summary>
    public bool IsClient => _isClient;

    /// <summary>Gets the local endpoint selected for the connection.</summary>
    public IPEndPoint LocalEndPoint => _transport.LocalEndPoint;

    /// <summary>Gets the connected peer's endpoint.</summary>
    public IPEndPoint RemoteEndPoint => _transport.RemoteEndPoint;

    /// <summary>Gets the negotiated application protocol.</summary>
    public SslApplicationProtocol NegotiatedApplicationProtocol =>
        _transport.NegotiatedApplicationProtocol;

    /// <summary>Gets the certificate presented and accepted for the peer.</summary>
    public X509Certificate? RemoteCertificate => _transport.RemoteCertificate;

    /// <summary>Connects to and authenticates a mutual QUIC connection.</summary>
    public static async ValueTask<MutualQuicConnection> ConnectAsync(
        EndPoint remoteEndPoint,
        string targetHost,
        MutualQuicConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetHost);
        ArgumentNullException.ThrowIfNull(options);

        X509Certificate2 localCertificate = options.LoadLocalCertificate();
        X509Certificate2? trustedPeerCertificate = null;
        try
        {
            trustedPeerCertificate = options.LoadTrustedPeerCertificate();
            var authenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = [options.ApplicationProtocol],
                ClientCertificates = [localCertificate],
                LocalCertificateSelectionCallback = (_, _, _, _, _) => localCertificate,
                RemoteCertificateValidationCallback =
                    MutualQuicConnectionOptions.CreatePinnedPeerValidator(trustedPeerCertificate),
                TargetHost = targetHost,
            };
            var connectionOptions = new QuicClientConnectionOptions
            {
                ClientAuthenticationOptions = authenticationOptions,
                DefaultCloseErrorCode = options.DefaultCloseErrorCode,
                DefaultStreamErrorCode = options.DefaultStreamErrorCode,
                HandshakeTimeout = options.HandshakeTimeout,
                IdleTimeout = options.IdleTimeout,
                MaxInboundBidirectionalStreams = options.MaximumInboundBidirectionalStreams,
                MaxInboundUnidirectionalStreams = 0,
                RemoteEndPoint = remoteEndPoint,
            };

            QuicConnection transport = await QuicConnection.ConnectAsync(
                connectionOptions,
                cancellationToken).ConfigureAwait(false);
            try
            {
                await InitiateProtocolHandshakeAsync(transport, cancellationToken)
                    .ConfigureAwait(false);
                return new MutualQuicConnection(
                    transport,
                    options,
                    isClient: true,
                    localCertificate,
                    trustedPeerCertificate);
            }
            catch
            {
                await transport.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            trustedPeerCertificate?.Dispose();
            localCertificate.Dispose();
            throw;
        }
    }

    /// <summary>Opens one dynamically named bidirectional application stream.</summary>
    public async ValueTask<NamedQuicStream> OpenStreamAsync(
        string name,
        NamedQuicStreamOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        QuicProtocol.ValidateName(name);
        options ??= new NamedQuicStreamOptions();
        var reservation = new NamedReservation(isLocal: true);
        lock (_namedSync)
        {
            if (!_namedStreams.TryAdd(name, reservation))
            {
                throw new InvalidOperationException($"The named QUIC stream '{name}' is already active.");
            }
        }

        QuicStream? transport = null;
        try
        {
            transport = await _transport.OpenOutboundStreamAsync(
                QuicStreamType.Bidirectional,
                cancellationToken).ConfigureAwait(false);
            lock (_namedSync)
            {
                reservation.Transport = transport;
                if (reservation.Superseded)
                {
                    throw new IOException(
                        $"The client-initiated stream won the simultaneous opening of '{name}'.");
                }
            }

            byte[] opening = QuicProtocol.CreateOpeningMessage(
                QuicProtocol.NamedStreamKind,
                name,
                options.Compression);
            await transport.WriteAsync(opening, cancellationToken).ConfigureAwait(false);
            byte[] response = new byte[1];
            await QuicProtocol.ReadExactlyAsync(transport, response, cancellationToken)
                .ConfigureAwait(false);
            if (response[0] != QuicProtocol.SuccessStatus)
            {
                throw CreateOpeningException(name, response[0]);
            }

            lock (_namedSync)
            {
                if (reservation.Superseded)
                {
                    throw new IOException(
                        $"The client-initiated stream won the simultaneous opening of '{name}'.");
                }
            }

            NamedQuicStream? result = null;
            result = new NamedQuicStream(
                name,
                transport,
                options.Compression,
                () => ReleaseNamedStream(name, reservation, result));
            reservation.Stream = result;
            transport = null;
            return result;
        }
        catch
        {
            if (transport is not null)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }

            ReleaseNamedStream(name, reservation, stream: null);
            throw;
        }
    }

    /// <summary>Accepts the next dynamically named application stream opened by the peer.</summary>
    public ValueTask<NamedQuicStream> AcceptStreamAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _acceptedNamedStreams.Reader.ReadAsync(cancellationToken);
    }

    /// <summary>Registers a caller-owned local random-access service under a dynamic name.</summary>
    public void RegisterRandomAccess(string name, ITeeRandomAccessStream randomAccess)
    {
        ThrowIfDisposed();
        QuicProtocol.ValidateName(name);
        ArgumentNullException.ThrowIfNull(randomAccess);
        lock (_randomAccessSync)
        {
            if (!_localRandomAccess.TryAdd(name, randomAccess))
            {
                throw new InvalidOperationException(
                    $"The random-access service '{name}' is already registered.");
            }
        }
    }

    /// <summary>Removes a local random-access registration without disposing its backing capability.</summary>
    public bool UnregisterRandomAccess(string name)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_randomAccessSync)
        {
            bool removed = _localRandomAccess.Remove(name);
            if (removed)
            {
                uint[] handles = _randomAccessSessions
                    .Where(pair => StringComparer.Ordinal.Equals(pair.Value.Name, name))
                    .Select(pair => pair.Key)
                    .ToArray();
                foreach (uint handle in handles)
                {
                    _randomAccessSessions.Remove(handle);
                }
            }

            return removed;
        }
    }

    /// <summary>Opens a proxy for a random-access service registered by the peer.</summary>
    public async ValueTask<QuicRandomAccessChannel> OpenRandomAccessAsync(
        string name,
        QuicRandomAccessOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        QuicProtocol.ValidateName(name);
        options ??= new QuicRandomAccessOptions();
        await using QuicStream stream = await _transport.OpenOutboundStreamAsync(
            QuicStreamType.Bidirectional,
            cancellationToken).ConfigureAwait(false);
        byte[] opening = QuicProtocol.CreateOpeningMessage(
            QuicProtocol.RandomAccessOpenKind,
            name,
            options.Compression,
            extraByteCount: sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(opening.AsSpan(opening.Length - sizeof(int)), options.CompressionThreshold);
        await stream.WriteAsync(opening, completeWrites: true, cancellationToken).ConfigureAwait(false);

        byte[] response = new byte[RandomAccessResponseSize];
        await QuicProtocol.ReadExactlyAsync(stream, response, cancellationToken).ConfigureAwait(false);
        if (response[0] != QuicProtocol.SuccessStatus)
        {
            throw CreateOpeningException(name, response[0]);
        }

        uint handle = BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(1));
        byte capabilities = response[5];
        int maximumRequestSize = BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(6));
        if (handle == 0 || maximumRequestSize <= 0)
        {
            throw new InvalidDataException("The peer returned invalid random-access channel metadata.");
        }

        return new QuicRandomAccessChannel(
            this,
            name,
            handle,
            options.Compression,
            options.CompressionThreshold,
            maximumRequestSize,
            canReadAt: (capabilities & 1) != 0,
            canWriteAt: (capabilities & 2) != 0);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            NamedQuicStream[] streams;
            lock (_namedSync)
            {
                streams = _namedStreams.Values
                    .Select(value => value.Stream)
                    .Where(stream => stream is not null)
                    .Cast<NamedQuicStream>()
                    .Distinct()
                    .ToArray();
            }

            foreach (NamedQuicStream stream in streams)
            {
                try
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Connection disposal below remains authoritative.
                }
            }

            _lifetimeCancellation.Cancel();
            _acceptedNamedStreams.Writer.TryComplete();
            try
            {
                await _transport.CloseAsync(_options.DefaultCloseErrorCode).ConfigureAwait(false);
            }
            catch (QuicException exception)
                when (exception.QuicError is QuicError.OperationAborted or QuicError.ConnectionAborted)
            {
            }

            await _transport.DisposeAsync().ConfigureAwait(false);
            await IgnoreExpectedPumpTerminationAsync(_inboundPump).ConfigureAwait(false);
            await AwaitInboundTasksDuringDisposalAsync().ConfigureAwait(false);
            _ownedLocalCertificate?.Dispose();
            _ownedTrustedPeerCertificate?.Dispose();
            _lifetimeCancellation.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    internal async ValueTask<int> ReadAtAsync(
        QuicRandomAccessChannel channel,
        Memory<byte> buffer,
        long offset,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateRandomAccessArguments(buffer.Length, offset, channel.MaximumRequestSize);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        await using QuicStream request = await _transport.OpenOutboundStreamAsync(
            QuicStreamType.Bidirectional,
            cancellationToken).ConfigureAwait(false);
        byte[] message = CreateRandomAccessRequest(
            channel.Handle,
            QuicProtocol.ReadOperation,
            flags: 0,
            offset,
            buffer.Length,
            payloadLength: 0);
        await request.WriteAsync(message, completeWrites: true, cancellationToken).ConfigureAwait(false);

        byte[] response = new byte[RandomAccessResponseSize];
        await QuicProtocol.ReadExactlyAsync(request, response, cancellationToken).ConfigureAwait(false);
        ThrowIfRandomAccessFailed(response[0]);
        byte flags = response[1];
        int rawCount = BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(2));
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(6));
        ValidateResponseLengths(rawCount, payloadLength, buffer.Length, channel.MaximumRequestSize);
        if ((flags & ~QuicProtocol.CompressedFlag) != 0)
        {
            throw new InvalidDataException("The peer returned unsupported random-access flags.");
        }

        byte[] payload = new byte[payloadLength];
        await QuicProtocol.ReadExactlyAsync(request, payload, cancellationToken).ConfigureAwait(false);
        if ((flags & QuicProtocol.CompressedFlag) != 0)
        {
            byte[] decompressed = await DecompressAsync(payload, rawCount, cancellationToken)
                .ConfigureAwait(false);
            decompressed.CopyTo(buffer);
        }
        else
        {
            if (payloadLength != rawCount)
            {
                throw new InvalidDataException("The peer returned inconsistent random-access lengths.");
            }

            payload.CopyTo(buffer);
        }

        return rawCount;
    }

    internal async ValueTask WriteAtAsync(
        QuicRandomAccessChannel channel,
        ReadOnlyMemory<byte> buffer,
        long offset,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateRandomAccessArguments(buffer.Length, offset, channel.MaximumRequestSize);
        (byte Flags, byte[] Payload) encoded = await EncodePayloadAsync(
            buffer,
            channel.Compression,
            channel.CompressionThreshold,
            cancellationToken).ConfigureAwait(false);
        byte[] header = CreateRandomAccessRequest(
            channel.Handle,
            QuicProtocol.WriteOperation,
            encoded.Flags,
            offset,
            buffer.Length,
            encoded.Payload.Length);
        byte[] message = new byte[header.Length + encoded.Payload.Length];
        header.CopyTo(message, 0);
        encoded.Payload.CopyTo(message, header.Length);

        await using QuicStream request = await _transport.OpenOutboundStreamAsync(
            QuicStreamType.Bidirectional,
            cancellationToken).ConfigureAwait(false);
        await request.WriteAsync(message, completeWrites: true, cancellationToken).ConfigureAwait(false);
        byte[] response = new byte[RandomAccessResponseSize];
        await QuicProtocol.ReadExactlyAsync(request, response, cancellationToken).ConfigureAwait(false);
        ThrowIfRandomAccessFailed(response[0]);
    }

    internal static async ValueTask AcceptProtocolHandshakeAsync(
        QuicConnection connection,
        CancellationToken cancellationToken)
    {
        await using QuicStream stream = await connection.AcceptInboundStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        if (stream.Type != QuicStreamType.Bidirectional)
        {
            throw new InvalidDataException("The TeeForge connection handshake must be bidirectional.");
        }

        byte[] received = new byte[QuicProtocol.ConnectionHandshake.Length];
        await QuicProtocol.ReadExactlyAsync(stream, received, cancellationToken).ConfigureAwait(false);
        if (!received.AsSpan().SequenceEqual(QuicProtocol.ConnectionHandshake))
        {
            throw new InvalidDataException("The peer sent an unsupported TeeForge connection handshake.");
        }

        await stream.WriteAsync(
            QuicProtocol.ConnectionHandshake,
            completeWrites: true,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InitiateProtocolHandshakeAsync(
        QuicConnection connection,
        CancellationToken cancellationToken)
    {
        await using QuicStream stream = await connection.OpenOutboundStreamAsync(
            QuicStreamType.Bidirectional,
            cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(
            QuicProtocol.ConnectionHandshake,
            completeWrites: true,
            cancellationToken).ConfigureAwait(false);
        byte[] received = new byte[QuicProtocol.ConnectionHandshake.Length];
        await QuicProtocol.ReadExactlyAsync(stream, received, cancellationToken).ConfigureAwait(false);
        if (!received.AsSpan().SequenceEqual(QuicProtocol.ConnectionHandshake))
        {
            throw new InvalidDataException("The peer sent an unsupported TeeForge connection handshake.");
        }
    }

    private async Task RunInboundPumpAsync(CancellationToken cancellationToken)
    {
        Exception? completionError = null;
        try
        {
            while (true)
            {
                QuicStream stream = await _transport.AcceptInboundStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                TrackInboundTask(ProcessInboundStreamAsync(stream, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (QuicException exception)
            when (exception.QuicError is QuicError.OperationAborted or QuicError.ConnectionAborted)
        {
        }
        catch (Exception exception)
        {
            completionError = exception;
        }
        finally
        {
            _acceptedNamedStreams.Writer.TryComplete(completionError);
        }
    }

    private async Task ProcessInboundStreamAsync(
        QuicStream stream,
        CancellationToken cancellationToken)
    {
        bool transferred = false;
        try
        {
            if (stream.Type != QuicStreamType.Bidirectional)
            {
                throw new InvalidDataException("TeeForge accepts only bidirectional application streams.");
            }

            byte kind = await QuicProtocol.ReadAndValidateCommonHeaderAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            transferred = kind switch
            {
                QuicProtocol.NamedStreamKind =>
                    await HandleInboundNamedStreamAsync(stream, cancellationToken).ConfigureAwait(false),
                QuicProtocol.RandomAccessOpenKind =>
                    await HandleRandomAccessOpenAsync(stream, cancellationToken).ConfigureAwait(false),
                QuicProtocol.RandomAccessRequestKind =>
                    await HandleRandomAccessRequestAsync(stream, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidDataException("The peer sent an unknown TeeForge QUIC stream kind."),
            };
        }
        finally
        {
            if (!transferred)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<bool> HandleInboundNamedStreamAsync(
        QuicStream transport,
        CancellationToken cancellationToken)
    {
        (QuicStreamCompression compression, string name) =
            await QuicProtocol.ReadOpeningAsync(transport, cancellationToken).ConfigureAwait(false);
        if (!_options.IsCompressionAllowed(compression))
        {
            await RejectStreamAsync(transport, QuicProtocol.CompressionRejectedStatus, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        var reservation = new NamedReservation(isLocal: false) { Transport = transport };
        QuicStream? losingServerStream = null;
        bool accepted;
        lock (_namedSync)
        {
            if (!_namedStreams.TryGetValue(name, out NamedReservation? existing))
            {
                _namedStreams.Add(name, reservation);
                accepted = true;
            }
            else if (!_isClient && existing.IsLocal)
            {
                existing.Superseded = true;
                losingServerStream = existing.Transport;
                _namedStreams[name] = reservation;
                accepted = true;
            }
            else
            {
                accepted = false;
            }
        }

        if (!accepted)
        {
            await RejectStreamAsync(transport, QuicProtocol.DuplicateStatus, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        losingServerStream?.Abort(QuicAbortDirection.Both, _options.DefaultStreamErrorCode);
        NamedQuicStream? result = null;
        try
        {
            await transport.WriteAsync(
                new byte[] { QuicProtocol.SuccessStatus },
                cancellationToken)
                .ConfigureAwait(false);
            result = new NamedQuicStream(
                name,
                transport,
                compression,
                () => ReleaseNamedStream(name, reservation, result));
            reservation.Stream = result;
            await _acceptedNamedStreams.Writer.WriteAsync(result, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch
        {
            if (result is not null)
            {
                await result.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                ReleaseNamedStream(name, reservation, stream: null);
            }

            throw;
        }
    }

    private async ValueTask<bool> HandleRandomAccessOpenAsync(
        QuicStream stream,
        CancellationToken cancellationToken)
    {
        (QuicStreamCompression compression, string name) =
            await QuicProtocol.ReadOpeningAsync(stream, cancellationToken).ConfigureAwait(false);
        byte[] thresholdBytes = new byte[sizeof(int)];
        await QuicProtocol.ReadExactlyAsync(stream, thresholdBytes, cancellationToken).ConfigureAwait(false);
        int threshold = BinaryPrimitives.ReadInt32LittleEndian(thresholdBytes);
        byte status = QuicProtocol.SuccessStatus;
        uint handle = 0;
        byte capabilities = 0;

        lock (_randomAccessSync)
        {
            if (!_options.IsCompressionAllowed(compression))
            {
                status = QuicProtocol.CompressionRejectedStatus;
            }
            else if (threshold <= 0)
            {
                status = QuicProtocol.InvalidRequestStatus;
            }
            else if (!_localRandomAccess.TryGetValue(name, out ITeeRandomAccessStream? backing))
            {
                status = QuicProtocol.NotFoundStatus;
            }
            else if (_randomAccessSessions.Count >= _options.MaximumRandomAccessSessions)
            {
                status = QuicProtocol.LimitReachedStatus;
            }
            else
            {
                handle = NextRandomAccessHandle();
                _randomAccessSessions.Add(
                    handle,
                    new RandomAccessSession(name, backing, compression, threshold));
                if (backing.CanReadAt)
                {
                    capabilities |= 1;
                }

                if (backing.CanWriteAt)
                {
                    capabilities |= 2;
                }
            }
        }

        byte[] response = new byte[RandomAccessResponseSize];
        response[0] = status;
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(1), handle);
        response[5] = capabilities;
        BinaryPrimitives.WriteInt32LittleEndian(
            response.AsSpan(6),
            _options.MaximumRandomAccessRequestSize);
        await stream.WriteAsync(response, completeWrites: true, cancellationToken).ConfigureAwait(false);
        return false;
    }

    private async ValueTask<bool> HandleRandomAccessRequestAsync(
        QuicStream stream,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[RandomAccessRequestHeaderSize];
        await QuicProtocol.ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        uint handle = BinaryPrimitives.ReadUInt32LittleEndian(header);
        byte operation = header[4];
        byte flags = header[5];
        long offset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(6));
        int rawLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(14));
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(18));
        byte[] response = new byte[RandomAccessResponseSize];

        try
        {
            RandomAccessSession session;
            lock (_randomAccessSync)
            {
                if (!_randomAccessSessions.TryGetValue(handle, out session!))
                {
                    throw new InvalidDataException("The random-access service handle is unknown.");
                }
            }

            ValidateRandomAccessArguments(rawLength, offset, _options.MaximumRandomAccessRequestSize);
            if ((flags & ~QuicProtocol.CompressedFlag) != 0)
            {
                throw new InvalidDataException("The random-access request contained unsupported flags.");
            }

            if (payloadLength < 0 ||
                payloadLength > checked(_options.MaximumRandomAccessRequestSize + 64 * 1024))
            {
                throw new InvalidDataException("The random-access payload length is invalid.");
            }

            if (operation == QuicProtocol.ReadOperation && session.Backing.CanReadAt)
            {
                if (flags != 0 || payloadLength != 0)
                {
                    throw new InvalidDataException("A positional-read request contained a payload.");
                }

                byte[] raw = new byte[rawLength];
                int count = await session.Backing.ReadAtAsync(raw, offset, cancellationToken)
                    .ConfigureAwait(false);
                if ((uint)count > (uint)raw.Length)
                {
                    throw new InvalidDataException("The local random-access source returned an invalid count.");
                }

                (byte Flags, byte[] Payload) encoded = await EncodePayloadAsync(
                    raw.AsMemory(0, count),
                    session.Compression,
                    session.CompressionThreshold,
                    cancellationToken).ConfigureAwait(false);
                response[1] = encoded.Flags;
                BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(2), count);
                BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(6), encoded.Payload.Length);
                await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(encoded.Payload, completeWrites: true, cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

            if (operation == QuicProtocol.WriteOperation && session.Backing.CanWriteAt)
            {
                byte[] payload = new byte[payloadLength];
                await QuicProtocol.ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
                byte[] raw;
                if ((flags & QuicProtocol.CompressedFlag) != 0)
                {
                    if (session.Compression == QuicStreamCompression.None ||
                        rawLength < session.CompressionThreshold)
                    {
                        throw new InvalidDataException("The request used unnegotiated compression.");
                    }

                    raw = await DecompressAsync(payload, rawLength, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    if (payloadLength != rawLength)
                    {
                        throw new InvalidDataException("The request contained inconsistent payload lengths.");
                    }

                    raw = payload;
                }

                await session.Backing.WriteAtAsync(raw, offset, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(response, completeWrites: true, cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

            throw new NotSupportedException("The requested positional operation is unavailable.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            response[0] = QuicProtocol.InvalidRequestStatus;
            await stream.WriteAsync(response, completeWrites: true, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }
    }

    private static byte[] CreateRandomAccessRequest(
        uint handle,
        byte operation,
        byte flags,
        long offset,
        int rawLength,
        int payloadLength)
    {
        byte[] message = new byte[QuicProtocol.CommonHeaderSize + RandomAccessRequestHeaderSize];
        QuicProtocol.WriteCommonHeader(message, QuicProtocol.RandomAccessRequestKind);
        Span<byte> header = message.AsSpan(QuicProtocol.CommonHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header, handle);
        header[4] = operation;
        header[5] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(header[6..], offset);
        BinaryPrimitives.WriteInt32LittleEndian(header[14..], rawLength);
        BinaryPrimitives.WriteInt32LittleEndian(header[18..], payloadLength);
        return message;
    }

    internal static async ValueTask<(byte Flags, byte[] Payload)> EncodePayloadAsync(
        ReadOnlyMemory<byte> payload,
        QuicStreamCompression compression,
        int threshold,
        CancellationToken cancellationToken)
    {
        if (compression == QuicStreamCompression.None || payload.Length < threshold)
        {
            return (0, payload.ToArray());
        }

        using var output = new MemoryStream();
        await using (var compressor = new BrotliStream(
            output,
            QuicProtocol.GetCompressionLevel(compression),
            leaveOpen: true))
        {
            await compressor.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        return (QuicProtocol.CompressedFlag, output.ToArray());
    }

    internal static async ValueTask<byte[]> DecompressAsync(
        ReadOnlyMemory<byte> payload,
        int expectedLength,
        CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(payload.ToArray(), writable: false);
        await using var decompressor = new BrotliStream(input, CompressionMode.Decompress);
        byte[] result = new byte[expectedLength];
        await QuicProtocol.ReadExactlyAsync(decompressor, result, cancellationToken)
            .ConfigureAwait(false);
        byte[] trailing = new byte[1];
        if (await decompressor.ReadAsync(trailing, cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException("The compressed payload expanded beyond its declared length.");
        }

        return result;
    }

    private void TrackInboundTask(Task task)
    {
        lock (_inboundTasksSync)
        {
            _inboundTasks.Add(task);
        }

        _ = task.ContinueWith(
            static (completed, state) =>
            {
                var owner = (MutualQuicConnection)state!;
                _ = completed.Exception;
                lock (owner._inboundTasksSync)
                {
                    owner._inboundTasks.Remove(completed);
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ReleaseNamedStream(
        string name,
        NamedReservation reservation,
        NamedQuicStream? stream)
    {
        lock (_namedSync)
        {
            if (_namedStreams.TryGetValue(name, out NamedReservation? current) &&
                ReferenceEquals(current, reservation) &&
                (stream is null || ReferenceEquals(current.Stream, stream)))
            {
                _namedStreams.Remove(name);
            }
        }
    }

    private uint NextRandomAccessHandle()
    {
        do
        {
            _nextRandomAccessHandle++;
        }
        while (_nextRandomAccessHandle == 0 ||
            _randomAccessSessions.ContainsKey(_nextRandomAccessHandle));
        return _nextRandomAccessHandle;
    }

    private static async ValueTask RejectStreamAsync(
        QuicStream stream,
        byte status,
        CancellationToken cancellationToken) =>
        await stream.WriteAsync(new byte[] { status }, completeWrites: true, cancellationToken)
            .ConfigureAwait(false);

    private static Exception CreateOpeningException(string name, byte status) =>
        status switch
        {
            QuicProtocol.DuplicateStatus =>
                new InvalidOperationException($"The peer already has an active endpoint named '{name}'."),
            QuicProtocol.NotFoundStatus =>
                new KeyNotFoundException($"The peer has no endpoint named '{name}'."),
            QuicProtocol.CompressionRejectedStatus =>
                new NotSupportedException($"The peer rejected compression for '{name}'."),
            QuicProtocol.LimitReachedStatus =>
                new IOException("The peer's QUIC service limit has been reached."),
            _ => new InvalidDataException("The peer rejected the TeeForge QUIC opening request."),
        };

    private static void ValidateRandomAccessArguments(int length, long offset, int maximumSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if ((uint)length > (uint)maximumSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                $"One random-access request cannot exceed {maximumSize} bytes.");
        }
    }

    private static void ValidateResponseLengths(
        int rawCount,
        int payloadLength,
        int requestedLength,
        int maximumSize)
    {
        if ((uint)rawCount > (uint)requestedLength ||
            payloadLength < 0 ||
            payloadLength > checked(maximumSize + 64 * 1024))
        {
            throw new InvalidDataException("The peer returned invalid random-access response lengths.");
        }
    }

    private static void ThrowIfRandomAccessFailed(byte status)
    {
        if (status != QuicProtocol.SuccessStatus)
        {
            throw new IOException("The peer could not complete the random-access operation.");
        }
    }

    private static async Task IgnoreExpectedPumpTerminationAsync(Task pump)
    {
        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (QuicException exception)
            when (exception.QuicError is QuicError.OperationAborted or QuicError.ConnectionAborted)
        {
        }
    }

    private async Task AwaitInboundTasksDuringDisposalAsync()
    {
        Task[] tasks;
        lock (_inboundTasksSync)
        {
            tasks = _inboundTasks.ToArray();
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // Closing the connection is expected to interrupt in-flight stream prefaces and requests.
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);

    private sealed class NamedReservation(bool isLocal)
    {
        internal bool IsLocal { get; } = isLocal;

        internal bool Superseded { get; set; }

        internal QuicStream? Transport { get; set; }

        internal NamedQuicStream? Stream { get; set; }
    }

    private sealed record RandomAccessSession(
        string Name,
        ITeeRandomAccessStream Backing,
        QuicStreamCompression Compression,
        int CompressionThreshold);
}
