namespace TeeForge.Networking;

/// <summary>Identifies transparent payload compression for a QUIC application stream.</summary>
public enum QuicStreamCompression : byte
{
    /// <summary>Leaves payload bytes uncompressed.</summary>
    None = 0,

    /// <summary>Uses Brotli with its fastest compression level.</summary>
    BrotliFastest = 1,

    /// <summary>Uses Brotli with its balanced optimal compression level.</summary>
    BrotliOptimal = 2,
}

/// <summary>Identifies the compression selections a receiving connection permits.</summary>
[Flags]
public enum QuicStreamCompressionAlgorithms
{
    /// <summary>Permits uncompressed streams.</summary>
    Uncompressed = 1,

    /// <summary>Permits Brotli at its fastest compression level.</summary>
    BrotliFastest = 2,

    /// <summary>Permits Brotli at its balanced optimal compression level.</summary>
    BrotliOptimal = 4,

    /// <summary>Permits every compression selection supported by TeeForge.</summary>
    All = Uncompressed | BrotliFastest | BrotliOptimal,
}
