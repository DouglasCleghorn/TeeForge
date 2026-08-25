using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Quic;
using System.Text;

namespace TeeForge.Networking;

internal static class QuicProtocol
{
    internal static readonly byte[] ConnectionHandshake = "TFQCONN\x01"u8.ToArray();
    internal const uint StreamMagic = 0x31514654;
    internal const byte Version = 1;
    internal const int CommonHeaderSize = 6;
    internal const int MaximumNameByteCount = 255;
    internal const byte NamedStreamKind = 1;
    internal const byte RandomAccessOpenKind = 2;
    internal const byte RandomAccessRequestKind = 3;
    internal const byte SuccessStatus = 0;
    internal const byte DuplicateStatus = 1;
    internal const byte NotFoundStatus = 2;
    internal const byte CompressionRejectedStatus = 3;
    internal const byte InvalidRequestStatus = 4;
    internal const byte LimitReachedStatus = 5;
    internal const byte ReadOperation = 1;
    internal const byte WriteOperation = 2;
    internal const byte CompressedFlag = 1;

    internal static byte[] CreateOpeningMessage(
        byte kind,
        string name,
        QuicStreamCompression compression,
        int extraByteCount = 0)
    {
        ValidateName(name);
        int nameByteCount = Encoding.UTF8.GetByteCount(name);
        byte[] message = new byte[CommonHeaderSize + 1 + 1 + nameByteCount + extraByteCount];
        WriteCommonHeader(message, kind);
        message[CommonHeaderSize] = (byte)compression;
        message[CommonHeaderSize + 1] = checked((byte)nameByteCount);
        Encoding.UTF8.GetBytes(name, message.AsSpan(CommonHeaderSize + 2, nameByteCount));
        return message;
    }

    internal static void WriteCommonHeader(Span<byte> destination, byte kind)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, StreamMagic);
        destination[4] = Version;
        destination[5] = kind;
    }

    internal static async ValueTask<(QuicStreamCompression Compression, string Name)> ReadOpeningAsync(
        QuicStream stream,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[2];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var compression = (QuicStreamCompression)header[0];
        ValidateCompression(compression, nameof(compression));
        int nameLength = header[1];
        if (nameLength == 0)
        {
            throw new InvalidDataException("A QUIC application name must not be empty.");
        }

        byte[] nameBytes = new byte[nameLength];
        await ReadExactlyAsync(stream, nameBytes, cancellationToken).ConfigureAwait(false);
        string name = new UTF8Encoding(false, true).GetString(nameBytes);
        ValidateName(name);
        return (compression, name);
    }

    internal static async ValueTask<byte> ReadAndValidateCommonHeaderAsync(
        QuicStream stream,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[CommonHeaderSize];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != StreamMagic || header[4] != Version)
        {
            throw new InvalidDataException("The peer sent an unsupported TeeForge QUIC stream preface.");
        }

        return header[5];
    }

    internal static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The peer closed before sending a complete protocol message.");
            }

            offset += read;
        }
    }

    internal static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        int byteCount = Encoding.UTF8.GetByteCount(name);
        if (byteCount > MaximumNameByteCount)
        {
            throw new ArgumentException(
                $"A QUIC application name cannot exceed {MaximumNameByteCount} UTF-8 bytes.",
                nameof(name));
        }
    }

    internal static void ValidateCompression(QuicStreamCompression compression, string parameterName)
    {
        if (compression is < QuicStreamCompression.None or > QuicStreamCompression.BrotliOptimal)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    internal static CompressionLevel GetCompressionLevel(QuicStreamCompression compression) =>
        compression switch
        {
            QuicStreamCompression.BrotliFastest => CompressionLevel.Fastest,
            QuicStreamCompression.BrotliOptimal => CompressionLevel.Optimal,
            _ => throw new ArgumentOutOfRangeException(nameof(compression)),
        };
}
