using System.Buffers.Binary;
using System.Text;

namespace TeeForge.Networking.Internal;

internal static class MultipathControlProtocol
{
    private const uint Magic = 0x54464D43;
    private const byte Version = 1;
    private const int HeaderSize = 8;
    private const int MaximumBodySize = HeaderSize + byte.MaxValue + ushort.MaxValue + 3;

    internal static byte[] Encode(MultipathControlMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Kind switch
        {
            MultipathControlMessageKind.PathReceivingValidFrames => EncodePathReceivingValidFrames(message),
            MultipathControlMessageKind.ModeChangeRequest => EncodeModeChange(message),
            MultipathControlMessageKind.EndpointAdvertisement => EncodeEndpoint(message),
            _ => throw new ArgumentOutOfRangeException(nameof(message)),
        };
    }

    internal static async ValueTask<MultipathControlMessage?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] prefix = new byte[sizeof(int)];
        if (!await TryReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        int length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length < HeaderSize || length > MaximumBodySize)
        {
            throw new InvalidDataException("The multipath control-frame length is invalid.");
        }

        byte[] body = new byte[length];
        await ReadExactlyAsync(stream, body, cancellationToken).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32BigEndian(body) != Magic || body[4] != Version || body[6] != 0 || body[7] != 0)
        {
            throw new InvalidDataException("The multipath control-frame header is invalid or unsupported.");
        }

        MultipathControlMessageKind kind = (MultipathControlMessageKind)body[5];
        return kind switch
        {
            MultipathControlMessageKind.PathReceivingValidFrames => DecodePathReceivingValidFrames(body),
            MultipathControlMessageKind.ModeChangeRequest => DecodeModeChange(body),
            MultipathControlMessageKind.EndpointAdvertisement => DecodeEndpoint(body),
            _ => throw new InvalidDataException("The multipath control message kind is unsupported."),
        };
    }

    private static byte[] EncodePathReceivingValidFrames(MultipathControlMessage message)
    {
        byte[] frame = CreateFrame(HeaderSize + 16, message.Kind);
        message.GetPathReceivingValidFrames().TryWriteBytes(frame.AsSpan(sizeof(int) + HeaderSize, 16), bigEndian: true, out _);
        return frame;
    }

    private static byte[] EncodeModeChange(MultipathControlMessage message)
    {
        byte[] frame = CreateFrame(HeaderSize + 4, message.Kind);
        Span<byte> payload = frame.AsSpan(sizeof(int) + HeaderSize);
        MultipathModeChangeRequest request = message.GetModeChangeRequest();
        payload[0] = (byte)request.Mode;
        payload[1] = checked((byte)request.DataShardCount);
        payload[2] = checked((byte)request.ParityShardCount);
        return frame;
    }

    private static byte[] EncodeEndpoint(MultipathControlMessage message)
    {
        MultipathEndpointAdvertisement advertisement = message.GetEndpointAdvertisement();
        byte[] scheme = Encoding.UTF8.GetBytes(advertisement.Scheme);
        byte[] endpoint = advertisement.Data.ToArray();
        byte[] frame = CreateFrame(
            checked(HeaderSize + 1 + scheme.Length + sizeof(ushort) + endpoint.Length),
            message.Kind);
        Span<byte> payload = frame.AsSpan(sizeof(int) + HeaderSize);
        payload[0] = checked((byte)scheme.Length);
        scheme.CopyTo(payload[1..]);
        BinaryPrimitives.WriteUInt16BigEndian(payload.Slice(1 + scheme.Length, sizeof(ushort)), checked((ushort)endpoint.Length));
        endpoint.CopyTo(payload[(1 + scheme.Length + sizeof(ushort))..]);
        return frame;
    }

    private static MultipathControlMessage DecodePathReceivingValidFrames(ReadOnlySpan<byte> body)
    {
        if (body.Length != HeaderSize + 16)
        {
            throw new InvalidDataException("The path-observation control message has an invalid length.");
        }

        return MultipathControlMessage.CreatePathReceivingValidFrames(new Guid(body.Slice(HeaderSize, 16), bigEndian: true));
    }

    private static MultipathControlMessage DecodeModeChange(ReadOnlySpan<byte> body)
    {
        if (body.Length != HeaderSize + 4 || body[HeaderSize + 3] != 0)
        {
            throw new InvalidDataException("The mode-change control message has an invalid length.");
        }

        try
        {
            return MultipathControlMessage.CreateModeChangeRequest(
                (MultipathStreamMode)body[HeaderSize],
                body[HeaderSize + 1],
                body[HeaderSize + 2]);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The mode-change control message is invalid.", exception);
        }
    }

    private static MultipathControlMessage DecodeEndpoint(ReadOnlySpan<byte> body)
    {
        if (body.Length < HeaderSize + 3)
        {
            throw new InvalidDataException("The endpoint control message is too short.");
        }

        int schemeLength = body[HeaderSize];
        int lengthOffset = HeaderSize + 1 + schemeLength;
        if (lengthOffset > body.Length - sizeof(ushort))
        {
            throw new InvalidDataException("The endpoint control message contains an invalid scheme length.");
        }

        int endpointLength = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(lengthOffset, sizeof(ushort)));
        int endpointOffset = lengthOffset + sizeof(ushort);
        if (endpointLength != body.Length - endpointOffset)
        {
            throw new InvalidDataException("The endpoint control message contains an invalid payload length.");
        }

        string scheme;
        try
        {
            scheme = new UTF8Encoding(false, true).GetString(body.Slice(HeaderSize + 1, schemeLength));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The endpoint scheme is not valid UTF-8.", exception);
        }

        return MultipathControlMessage.CreateEndpointAdvertisement(scheme, body.Slice(endpointOffset).ToArray());
    }

    private static byte[] CreateFrame(int bodyLength, MultipathControlMessageKind kind)
    {
        byte[] frame = new byte[checked(sizeof(int) + bodyLength)];
        BinaryPrimitives.WriteInt32BigEndian(frame, bodyLength);
        Span<byte> body = frame.AsSpan(sizeof(int));
        BinaryPrimitives.WriteUInt32BigEndian(body, Magic);
        body[4] = Version;
        body[5] = (byte)kind;
        return frame;
    }

    private static async ValueTask<bool> TryReadExactlyAsync(
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
                if (offset == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("A multipath control frame ended before all bytes arrived.");
            }

            offset += read;
        }

        return true;
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        if (!await TryReadExactlyAsync(stream, buffer, cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("A multipath control frame ended before all bytes arrived.");
        }
    }
}
