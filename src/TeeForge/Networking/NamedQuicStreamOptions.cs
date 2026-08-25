namespace TeeForge.Networking;

/// <summary>Provides immutable options for opening one named QUIC stream.</summary>
public class NamedQuicStreamOptions
{
    /// <summary>Initializes a new options instance.</summary>
    /// <param name="compression">The transparent payload compression requested from the peer.</param>
    public NamedQuicStreamOptions(QuicStreamCompression compression = QuicStreamCompression.None)
    {
        QuicProtocol.ValidateCompression(compression, nameof(compression));
        Compression = compression;
    }

    /// <summary>Gets the transparent payload compression requested from the peer.</summary>
    public QuicStreamCompression Compression { get; }
}
