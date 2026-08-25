namespace TeeForge.Networking;

/// <summary>Provides immutable options for opening a remote random-access service.</summary>
public class QuicRandomAccessOptions
{
    /// <summary>Initializes a new options instance.</summary>
    /// <param name="compression">The compression used for qualifying request and response payloads.</param>
    /// <param name="compressionThreshold">
    /// The minimum uncompressed payload size at which compression is applied.
    /// </param>
    public QuicRandomAccessOptions(
        QuicStreamCompression compression = QuicStreamCompression.None,
        int compressionThreshold = 16 * 1024)
    {
        QuicProtocol.ValidateCompression(compression, nameof(compression));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(compressionThreshold);
        Compression = compression;
        CompressionThreshold = compressionThreshold;
    }

    /// <summary>Gets the compression used for qualifying request and response payloads.</summary>
    public QuicStreamCompression Compression { get; }

    /// <summary>Gets the minimum uncompressed payload size at which compression is applied.</summary>
    public int CompressionThreshold { get; }
}
