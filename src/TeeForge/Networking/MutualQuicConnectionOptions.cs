using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TeeForge.Networking;

/// <summary>Provides immutable file-based identity and transport options for a mutual QUIC connection.</summary>
public class MutualQuicConnectionOptions
{
    private const long MaximumQuicApplicationErrorCode = (1L << 62) - 1;

    /// <summary>Initializes a new options instance.</summary>
    /// <param name="localCertificatePath">The path to the local X.509 certificate PEM file.</param>
    /// <param name="localPrivateKeyPath">The path to the matching unencrypted private-key PEM file.</param>
    /// <param name="trustedPeerCertificatePath">
    /// The path to the X.509 certificate file pinned for the peer. PEM and DER certificates are supported.
    /// </param>
    /// <param name="applicationProtocol">The ALPN protocol negotiated by both peers.</param>
    /// <param name="idleTimeout">
    /// The idle timeout, or <see cref="TimeSpan.Zero"/> to use the native QUIC implementation's default.
    /// </param>
    /// <param name="handshakeTimeout">The maximum TLS handshake duration.</param>
    /// <param name="defaultCloseErrorCode">The application error code used to close a connection.</param>
    /// <param name="defaultStreamErrorCode">The application error code used to abort a stream.</param>
    /// <param name="maximumInboundBidirectionalStreams">
    /// The maximum number of concurrently active bidirectional streams accepted from the peer.
    /// </param>
    /// <param name="maximumPendingNamedStreams">
    /// The maximum number of accepted named streams waiting for application acceptance.
    /// </param>
    /// <param name="maximumRandomAccessRequestSize">
    /// The largest buffer accepted by one remote positional operation.
    /// </param>
    /// <param name="maximumRandomAccessSessions">
    /// The maximum number of negotiated remote random-access channels served by this connection.
    /// </param>
    /// <param name="allowedCompressions">The compression selections accepted from the peer.</param>
    public MutualQuicConnectionOptions(
        string localCertificatePath,
        string localPrivateKeyPath,
        string trustedPeerCertificatePath,
        SslApplicationProtocol applicationProtocol,
        TimeSpan? idleTimeout = null,
        TimeSpan? handshakeTimeout = null,
        long defaultCloseErrorCode = 0,
        long defaultStreamErrorCode = 0,
        int maximumInboundBidirectionalStreams = 100,
        int maximumPendingNamedStreams = 100,
        int maximumRandomAccessRequestSize = 1024 * 1024,
        int maximumRandomAccessSessions = 100,
        QuicStreamCompressionAlgorithms allowedCompressions = QuicStreamCompressionAlgorithms.All)
    {
        LocalCertificatePath = ValidateLocalFile(localCertificatePath, nameof(localCertificatePath));
        LocalPrivateKeyPath = ValidateLocalFile(localPrivateKeyPath, nameof(localPrivateKeyPath));
        TrustedPeerCertificatePath = ValidateLocalFile(
            trustedPeerCertificatePath,
            nameof(trustedPeerCertificatePath));

        if (applicationProtocol.Protocol.IsEmpty)
        {
            throw new ArgumentException("The application protocol must not be empty.", nameof(applicationProtocol));
        }

        TimeSpan resolvedIdleTimeout = idleTimeout ?? TimeSpan.Zero;
        TimeSpan resolvedHandshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(10);
        if (resolvedIdleTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        }

        if (resolvedHandshakeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(handshakeTimeout));
        }

        ValidateApplicationErrorCode(defaultCloseErrorCode, nameof(defaultCloseErrorCode));
        ValidateApplicationErrorCode(defaultStreamErrorCode, nameof(defaultStreamErrorCode));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumInboundBidirectionalStreams);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPendingNamedStreams);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRandomAccessRequestSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRandomAccessSessions);
        if ((allowedCompressions & ~QuicStreamCompressionAlgorithms.All) != 0 ||
            allowedCompressions == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(allowedCompressions));
        }

        ApplicationProtocol = applicationProtocol;
        IdleTimeout = resolvedIdleTimeout;
        HandshakeTimeout = resolvedHandshakeTimeout;
        DefaultCloseErrorCode = defaultCloseErrorCode;
        DefaultStreamErrorCode = defaultStreamErrorCode;
        MaximumInboundBidirectionalStreams = maximumInboundBidirectionalStreams;
        MaximumPendingNamedStreams = maximumPendingNamedStreams;
        MaximumRandomAccessRequestSize = maximumRandomAccessRequestSize;
        MaximumRandomAccessSessions = maximumRandomAccessSessions;
        AllowedCompressions = allowedCompressions;
    }

    /// <summary>Gets the absolute path to the local X.509 certificate PEM file.</summary>
    public string LocalCertificatePath { get; }

    /// <summary>Gets the absolute path to the matching unencrypted private-key PEM file.</summary>
    public string LocalPrivateKeyPath { get; }

    /// <summary>Gets the absolute path to the certificate pinned for the peer.</summary>
    public string TrustedPeerCertificatePath { get; }

    /// <summary>Gets the ALPN protocol negotiated by both peers.</summary>
    public SslApplicationProtocol ApplicationProtocol { get; }

    /// <summary>Gets the connection idle timeout.</summary>
    public TimeSpan IdleTimeout { get; }

    /// <summary>Gets the maximum TLS handshake duration.</summary>
    public TimeSpan HandshakeTimeout { get; }

    /// <summary>Gets the application error code used to close a connection.</summary>
    public long DefaultCloseErrorCode { get; }

    /// <summary>Gets the application error code used to abort a stream.</summary>
    public long DefaultStreamErrorCode { get; }

    /// <summary>Gets the maximum concurrently active bidirectional stream count accepted from the peer.</summary>
    public int MaximumInboundBidirectionalStreams { get; }

    /// <summary>Gets the maximum accepted named streams waiting for application acceptance.</summary>
    public int MaximumPendingNamedStreams { get; }

    /// <summary>Gets the largest buffer accepted by one remote random-access operation.</summary>
    public int MaximumRandomAccessRequestSize { get; }

    /// <summary>Gets the maximum negotiated remote random-access channels served by the connection.</summary>
    public int MaximumRandomAccessSessions { get; }

    /// <summary>Gets the compression selections accepted from the peer.</summary>
    public QuicStreamCompressionAlgorithms AllowedCompressions { get; }

    internal bool IsCompressionAllowed(QuicStreamCompression compression) =>
        (AllowedCompressions & compression switch
        {
            QuicStreamCompression.None => QuicStreamCompressionAlgorithms.Uncompressed,
            QuicStreamCompression.BrotliFastest => QuicStreamCompressionAlgorithms.BrotliFastest,
            QuicStreamCompression.BrotliOptimal => QuicStreamCompressionAlgorithms.BrotliOptimal,
            _ => 0,
        }) != 0;

    internal X509Certificate2 LoadLocalCertificate()
    {
        X509Certificate2 certificate = X509Certificate2.CreateFromPemFile(
            LocalCertificatePath,
            LocalPrivateKeyPath);
        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new CryptographicException("The local certificate and private-key files did not produce a private key.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return certificate;
        }

        using (certificate)
        {
            const string password = "TeeForge-MutualQuicConnection";
            byte[] pkcs12 = certificate.Export(X509ContentType.Pkcs12, password);
            try
            {
                return X509CertificateLoader.LoadPkcs12(
                    pkcs12,
                    password,
                    X509KeyStorageFlags.DefaultKeySet);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs12);
            }
        }
    }

    internal X509Certificate2 LoadTrustedPeerCertificate() =>
        X509CertificateLoader.LoadCertificateFromFile(TrustedPeerCertificatePath);

    internal static RemoteCertificateValidationCallback CreatePinnedPeerValidator(
        X509Certificate2 trustedPeerCertificate)
    {
        byte[] trustedHash = trustedPeerCertificate.GetCertHash(HashAlgorithmName.SHA256);
        DateTime notBeforeUtc = trustedPeerCertificate.NotBefore.ToUniversalTime();
        DateTime notAfterUtc = trustedPeerCertificate.NotAfter.ToUniversalTime();

        return (_, certificate, _, _) =>
        {
            if (certificate is null)
            {
                return false;
            }

            DateTime now = DateTime.UtcNow;
            byte[] actualHash = certificate.GetCertHash(HashAlgorithmName.SHA256);
            return now >= notBeforeUtc &&
                now <= notAfterUtc &&
                CryptographicOperations.FixedTimeEquals(actualHash, trustedHash);
        };
    }

    private static string ValidateLocalFile(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The required local key or certificate file was not found.", fullPath);
        }

        return fullPath;
    }

    private static void ValidateApplicationErrorCode(long errorCode, string parameterName)
    {
        if ((ulong)errorCode > MaximumQuicApplicationErrorCode)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
