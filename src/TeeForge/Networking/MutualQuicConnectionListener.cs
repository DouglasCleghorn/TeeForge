using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace TeeForge.Networking;

/// <summary>Listens for mutually authenticated QUIC connections.</summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class MutualQuicConnectionListener : IAsyncDisposable
{
    private readonly QuicListener _listener;
    private readonly MutualQuicConnectionOptions _options;
    private readonly X509Certificate2 _localCertificate;
    private readonly X509Certificate2 _trustedPeerCertificate;
    private int _disposeState;

    private MutualQuicConnectionListener(
        QuicListener listener,
        MutualQuicConnectionOptions options,
        X509Certificate2 localCertificate,
        X509Certificate2 trustedPeerCertificate)
    {
        _listener = listener;
        _options = options;
        _localCertificate = localCertificate;
        _trustedPeerCertificate = trustedPeerCertificate;
    }

    /// <summary>Gets whether the current platform supports QUIC listeners.</summary>
    public static bool IsSupported => QuicListener.IsSupported;

    /// <summary>Gets the endpoint on which the listener accepts connections.</summary>
    public IPEndPoint LocalEndPoint => _listener.LocalEndPoint;

    /// <summary>Creates and starts a mutually authenticated QUIC listener.</summary>
    public static async ValueTask<MutualQuicConnectionListener> ListenAsync(
        IPEndPoint listenEndPoint,
        MutualQuicConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listenEndPoint);
        ArgumentNullException.ThrowIfNull(options);

        X509Certificate2 localCertificate = options.LoadLocalCertificate();
        X509Certificate2? trustedPeerCertificate = null;
        try
        {
            trustedPeerCertificate = options.LoadTrustedPeerCertificate();
            RemoteCertificateValidationCallback peerValidator =
                MutualQuicConnectionOptions.CreatePinnedPeerValidator(trustedPeerCertificate);
            var listenerOptions = new QuicListenerOptions
            {
                ListenEndPoint = listenEndPoint,
                ApplicationProtocols = [options.ApplicationProtocol],
                ConnectionOptionsCallback = (_, _, _) =>
                    ValueTask.FromResult(new QuicServerConnectionOptions
                    {
                        DefaultCloseErrorCode = options.DefaultCloseErrorCode,
                        DefaultStreamErrorCode = options.DefaultStreamErrorCode,
                        HandshakeTimeout = options.HandshakeTimeout,
                        IdleTimeout = options.IdleTimeout,
                        MaxInboundBidirectionalStreams = options.MaximumInboundBidirectionalStreams,
                        MaxInboundUnidirectionalStreams = 0,
                        ServerAuthenticationOptions = new SslServerAuthenticationOptions
                        {
                            ApplicationProtocols = [options.ApplicationProtocol],
                            ClientCertificateRequired = true,
                            RemoteCertificateValidationCallback = peerValidator,
                            ServerCertificate = localCertificate,
                        },
                    }),
            };

            QuicListener listener = await QuicListener.ListenAsync(listenerOptions, cancellationToken)
                .ConfigureAwait(false);
            return new MutualQuicConnectionListener(
                listener,
                options,
                localCertificate,
                trustedPeerCertificate);
        }
        catch
        {
            trustedPeerCertificate?.Dispose();
            localCertificate.Dispose();
            throw;
        }
    }

    /// <summary>Accepts and completes the TeeForge handshake for one authenticated connection.</summary>
    public async ValueTask<MutualQuicConnection> AcceptConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        QuicConnection transport = await _listener.AcceptConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await MutualQuicConnection.AcceptProtocolHandshakeAsync(transport, cancellationToken)
                .ConfigureAwait(false);
            return new MutualQuicConnection(transport, _options, isClient: false);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            try
            {
                await _listener.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _localCertificate.Dispose();
                _trustedPeerCertificate.Dispose();
            }
        }

        GC.SuppressFinalize(this);
    }
}
