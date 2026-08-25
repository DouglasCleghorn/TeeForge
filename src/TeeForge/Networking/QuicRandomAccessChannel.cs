using System.Runtime.Versioning;
using TeeForge.RandomAccess;

namespace TeeForge.Networking;

/// <summary>Provides position-independent I/O through a named remote QUIC service.</summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class QuicRandomAccessChannel : ITeeRandomAccessStream
{
    private readonly MutualQuicConnection _connection;

    internal QuicRandomAccessChannel(
        MutualQuicConnection connection,
        string name,
        uint handle,
        QuicStreamCompression compression,
        int compressionThreshold,
        int maximumRequestSize,
        bool canReadAt,
        bool canWriteAt)
    {
        _connection = connection;
        Name = name;
        Handle = handle;
        Compression = compression;
        CompressionThreshold = compressionThreshold;
        MaximumRequestSize = maximumRequestSize;
        CanReadAt = canReadAt;
        CanWriteAt = canWriteAt;
    }

    /// <summary>Gets the dynamic service name used to open this channel.</summary>
    public string Name { get; }

    /// <summary>Gets the negotiated compression for qualifying request and response payloads.</summary>
    public QuicStreamCompression Compression { get; }

    /// <summary>Gets the minimum uncompressed payload size at which compression is applied.</summary>
    public int CompressionThreshold { get; }

    /// <summary>Gets the maximum buffer size permitted for one positional operation.</summary>
    public int MaximumRequestSize { get; }

    /// <inheritdoc/>
    public bool CanReadAt { get; }

    /// <inheritdoc/>
    public bool CanWriteAt { get; }

    internal uint Handle { get; }

    /// <inheritdoc/>
    public int ReadAt(Span<byte> buffer, long offset)
    {
        if (!CanReadAt)
        {
            throw new NotSupportedException("The remote service does not support positional reads.");
        }

        byte[] temporary = new byte[buffer.Length];
        int count = ReadAtAsync(temporary, offset).AsTask().GetAwaiter().GetResult();
        temporary.AsSpan(0, count).CopyTo(buffer);
        return count;
    }

    /// <inheritdoc/>
    public ValueTask<int> ReadAtAsync(
        Memory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default)
    {
        if (!CanReadAt)
        {
            throw new NotSupportedException("The remote service does not support positional reads.");
        }

        return _connection.ReadAtAsync(this, buffer, offset, cancellationToken);
    }

    /// <inheritdoc/>
    public void WriteAt(ReadOnlySpan<byte> buffer, long offset)
    {
        if (!CanWriteAt)
        {
            throw new NotSupportedException("The remote service does not support positional writes.");
        }

        WriteAtAsync(buffer.ToArray(), offset).AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc/>
    public ValueTask WriteAtAsync(
        ReadOnlyMemory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default)
    {
        if (!CanWriteAt)
        {
            throw new NotSupportedException("The remote service does not support positional writes.");
        }

        return _connection.WriteAtAsync(this, buffer, offset, cancellationToken);
    }
}
