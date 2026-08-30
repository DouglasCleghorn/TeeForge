using System.Buffers.Binary;
using System.IO.Hashing;

namespace TeeForge.Networking.Internal;

internal static class MultipathProtocol
{
    private const uint Magic = 0x54464D50;
    private const byte Version = 1;
    private const int CommonHeaderSize = 8;
    private const int HelloFrameSize = CommonHeaderSize + 32;
    private const int DataHeaderSize = CommonHeaderSize + 52;
    private const int CompleteFrameSize = CommonHeaderSize + 24;
    private const int RetireFrameSize = CommonHeaderSize + 24;
    private const int MaximumFramePayloadSize = 1024 * 1024;

    internal static byte[] CreateHelloFrame(Guid sessionId, Guid pathId)
    {
        byte[] frame = CreateFrame(HelloFrameSize, MultipathFrameType.Hello);
        Span<byte> body = frame.AsSpan(sizeof(int));
        WriteGuid(body.Slice(CommonHeaderSize, 16), sessionId);
        WriteGuid(body.Slice(CommonHeaderSize + 16, 16), pathId);
        return frame;
    }

    internal static byte[] CreateDataFrame(
        Guid sessionId,
        ulong epoch,
        ulong sequence,
        MultipathStreamMode mode,
        byte shardIndex,
        byte dataShardCount,
        byte parityShardCount,
        int logicalLength,
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumFramePayloadSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        byte[] frame = CreateFrame(checked(DataHeaderSize + payload.Length), MultipathFrameType.Data);
        Span<byte> body = frame.AsSpan(sizeof(int));
        WriteGuid(body.Slice(CommonHeaderSize, 16), sessionId);
        BinaryPrimitives.WriteUInt64BigEndian(body.Slice(24, 8), epoch);
        BinaryPrimitives.WriteUInt64BigEndian(body.Slice(32, 8), sequence);
        body[40] = (byte)mode;
        body[41] = shardIndex;
        body[42] = dataShardCount;
        body[43] = parityShardCount;
        BinaryPrimitives.WriteInt32BigEndian(body.Slice(44, 4), logicalLength);
        BinaryPrimitives.WriteInt32BigEndian(body.Slice(48, 4), payload.Length);
        BinaryPrimitives.WriteUInt64BigEndian(body.Slice(52, 8), XxHash64.HashToUInt64(payload));
        payload.CopyTo(body.Slice(DataHeaderSize));
        return frame;
    }

    internal static byte[] CreateCompleteFrame(Guid sessionId, ulong finalSequence)
    {
        byte[] frame = CreateFrame(CompleteFrameSize, MultipathFrameType.Complete);
        Span<byte> body = frame.AsSpan(sizeof(int));
        WriteGuid(body.Slice(CommonHeaderSize, 16), sessionId);
        BinaryPrimitives.WriteUInt64BigEndian(body.Slice(24, 8), finalSequence);
        return frame;
    }

    internal static byte[] CreateRetireFrame(Guid sessionId, ulong epoch)
    {
        byte[] frame = CreateFrame(RetireFrameSize, MultipathFrameType.Retire);
        Span<byte> body = frame.AsSpan(sizeof(int));
        WriteGuid(body.Slice(CommonHeaderSize, 16), sessionId);
        BinaryPrimitives.WriteUInt64BigEndian(body.Slice(24, 8), epoch);
        return frame;
    }

    internal static async ValueTask<MultipathHello> ReadHelloAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        MultipathRawFrame raw = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        if (raw.Type != MultipathFrameType.Hello || raw.Body.Length != HelloFrameSize)
        {
            throw new InvalidDataException("The path did not begin with a valid multipath hello frame.");
        }

        ReadOnlySpan<byte> body = raw.Body;
        var hello = new MultipathHello(
            ReadGuid(body.Slice(CommonHeaderSize, 16)),
            ReadGuid(body.Slice(CommonHeaderSize + 16, 16)));
        if (hello.SessionId == Guid.Empty || hello.PathId == Guid.Empty)
        {
            throw new InvalidDataException("The multipath hello contains an empty identifier.");
        }

        return hello;
    }

    internal static async ValueTask<MultipathReceivedFrame> ReadDataOrCompleteAsync(
        Stream stream,
        Guid expectedSessionId,
        Guid pathId,
        CancellationToken cancellationToken)
    {
        MultipathRawFrame raw = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        ReadOnlySpan<byte> body = raw.Body;

        if (raw.Type == MultipathFrameType.Complete)
        {
            if (body.Length != CompleteFrameSize)
            {
                throw new InvalidDataException("The path contained an invalid completion frame.");
            }

            ValidateSession(body, expectedSessionId);
            return MultipathReceivedFrame.CreateComplete(
                pathId,
                BinaryPrimitives.ReadUInt64BigEndian(body.Slice(24, 8)));
        }

        if (raw.Type == MultipathFrameType.Retire)
        {
            if (body.Length != RetireFrameSize)
            {
                throw new InvalidDataException("The path contained an invalid retirement frame.");
            }

            ValidateSession(body, expectedSessionId);
            return MultipathReceivedFrame.CreateRetired(
                pathId,
                BinaryPrimitives.ReadUInt64BigEndian(body.Slice(24, 8)));
        }

        if (raw.Type != MultipathFrameType.Data || body.Length < DataHeaderSize)
        {
            throw new InvalidDataException("The path contained an unexpected multipath frame.");
        }

        ValidateSession(body, expectedSessionId);
        MultipathStreamMode mode = (MultipathStreamMode)body[40];
        if (!Enum.IsDefined(mode))
        {
            throw new InvalidDataException("The data frame names an unsupported distribution mode.");
        }

        int logicalLength = BinaryPrimitives.ReadInt32BigEndian(body.Slice(44, 4));
        int payloadLength = BinaryPrimitives.ReadInt32BigEndian(body.Slice(48, 4));
        if (logicalLength < 0 || payloadLength < 0 ||
            payloadLength > MaximumFramePayloadSize || body.Length != DataHeaderSize + payloadLength)
        {
            throw new InvalidDataException("The data frame contains invalid lengths.");
        }

        byte dataShardCount = body[42];
        byte parityShardCount = body[43];
        byte shardIndex = body[41];
        ValidateShardMetadata(mode, shardIndex, dataShardCount, parityShardCount, logicalLength, payloadLength);

        byte[] payload = body.Slice(DataHeaderSize, payloadLength).ToArray();
        ulong expectedChecksum = BinaryPrimitives.ReadUInt64BigEndian(body.Slice(52, 8));
        if (XxHash64.HashToUInt64(payload) != expectedChecksum)
        {
            throw new InvalidDataException("The data frame payload checksum is invalid.");
        }

        return MultipathReceivedFrame.CreateData(
            pathId,
            BinaryPrimitives.ReadUInt64BigEndian(body.Slice(24, 8)),
            BinaryPrimitives.ReadUInt64BigEndian(body.Slice(32, 8)),
            mode,
            shardIndex,
            dataShardCount,
            parityShardCount,
            logicalLength,
            payload);
    }

    private static byte[] CreateFrame(int bodyLength, MultipathFrameType type)
    {
        byte[] frame = new byte[checked(sizeof(int) + bodyLength)];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, sizeof(int)), bodyLength);
        Span<byte> body = frame.AsSpan(sizeof(int));
        BinaryPrimitives.WriteUInt32BigEndian(body, Magic);
        body[4] = Version;
        body[5] = (byte)type;
        return frame;
    }

    private static async ValueTask<MultipathRawFrame> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] prefix = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, prefix, allowEndOfStream: false, cancellationToken).ConfigureAwait(false);
        int bodyLength = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (bodyLength < CommonHeaderSize || bodyLength > DataHeaderSize + MaximumFramePayloadSize)
        {
            throw new InvalidDataException("The path frame length is invalid.");
        }

        byte[] body = new byte[bodyLength];
        await ReadExactlyAsync(stream, body, allowEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32BigEndian(body) != Magic || body[4] != Version || body[6] != 0 || body[7] != 0)
        {
            throw new InvalidDataException("The path frame header is invalid or unsupported.");
        }

        MultipathFrameType type = (MultipathFrameType)body[5];
        return new MultipathRawFrame(type, body);
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        bool allowEndOfStream,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (allowEndOfStream && offset == 0)
                {
                    return;
                }

                throw new EndOfStreamException("A multipath frame ended before all bytes arrived.");
            }

            offset += read;
        }
    }

    private static void ValidateSession(ReadOnlySpan<byte> body, Guid expectedSessionId)
    {
        if (ReadGuid(body.Slice(CommonHeaderSize, 16)) != expectedSessionId)
        {
            throw new InvalidDataException("The data path changed multipath sessions.");
        }
    }

    private static void ValidateShardMetadata(
        MultipathStreamMode mode,
        byte shardIndex,
        byte dataShardCount,
        byte parityShardCount,
        int logicalLength,
        int payloadLength)
    {
        if (mode == MultipathStreamMode.ErasureCode)
        {
            int memberCount = dataShardCount + parityShardCount;
            if (dataShardCount < 2 || parityShardCount < 1 || shardIndex >= memberCount ||
                payloadLength == 0 || logicalLength > dataShardCount * payloadLength)
            {
                throw new InvalidDataException("The erasure data frame contains invalid shard metadata.");
            }
        }
        else if (shardIndex != 0 || dataShardCount != 1 || parityShardCount != 0 ||
            logicalLength != payloadLength)
        {
            throw new InvalidDataException("The non-erasure data frame contains invalid shard metadata.");
        }
    }

    private static void WriteGuid(Span<byte> destination, Guid value) =>
        value.TryWriteBytes(destination, bigEndian: true, out _);

    private static Guid ReadGuid(ReadOnlySpan<byte> source) => new(source, bigEndian: true);
}

internal enum MultipathFrameType : byte
{
    Hello = 1,
    Data = 2,
    Complete = 3,
    Retire = 4,
}

internal readonly record struct MultipathHello(Guid SessionId, Guid PathId);

internal readonly record struct MultipathRawFrame(MultipathFrameType Type, byte[] Body);

internal sealed class MultipathReceivedFrame
{
    private MultipathReceivedFrame(
        Guid pathId,
        ulong epoch,
        ulong sequence,
        MultipathStreamMode mode,
        byte shardIndex,
        byte dataShardCount,
        byte parityShardCount,
        int logicalLength,
        byte[]? payload,
        ulong? finalSequence,
        bool isRetired)
    {
        PathId = pathId;
        Epoch = epoch;
        Sequence = sequence;
        Mode = mode;
        ShardIndex = shardIndex;
        DataShardCount = dataShardCount;
        ParityShardCount = parityShardCount;
        LogicalLength = logicalLength;
        Payload = payload;
        FinalSequence = finalSequence;
        IsRetired = isRetired;
    }

    internal Guid PathId { get; }

    internal ulong Epoch { get; }

    internal ulong Sequence { get; }

    internal MultipathStreamMode Mode { get; }

    internal byte ShardIndex { get; }

    internal byte DataShardCount { get; }

    internal byte ParityShardCount { get; }

    internal int LogicalLength { get; }

    internal byte[]? Payload { get; }

    internal ulong? FinalSequence { get; }

    internal bool IsRetired { get; }

    internal static MultipathReceivedFrame CreateData(
        Guid pathId,
        ulong epoch,
        ulong sequence,
        MultipathStreamMode mode,
        byte shardIndex,
        byte dataShardCount,
        byte parityShardCount,
        int logicalLength,
        byte[] payload) =>
        new(
            pathId,
            epoch,
            sequence,
            mode,
            shardIndex,
            dataShardCount,
            parityShardCount,
            logicalLength,
            payload,
            null,
            false);

    internal static MultipathReceivedFrame CreateComplete(Guid pathId, ulong finalSequence) =>
        new(pathId, 0, 0, default, 0, 0, 0, 0, null, finalSequence, false);

    internal static MultipathReceivedFrame CreateRetired(Guid pathId, ulong epoch) =>
        new(pathId, epoch, 0, default, 0, 0, 0, 0, null, null, true);
}
