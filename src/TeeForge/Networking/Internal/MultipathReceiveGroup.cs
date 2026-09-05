using TeeForge.ErasureCoding.Internal;

namespace TeeForge.Networking.Internal;

internal sealed class MultipathReceiveGroup
{
    private readonly ulong _epoch;
    private readonly MultipathStreamMode _mode;
    private readonly int _logicalLength;
    private readonly byte[][] _shards;
    private readonly bool[] _present;
    private int _presentCount;
    private byte[]? _decoded;

    internal MultipathReceiveGroup(MultipathReceivedFrame frame)
    {
        _epoch = frame.Epoch;
        _mode = frame.Mode;
        _logicalLength = frame.LogicalLength;
        DataShardCount = frame.DataShardCount;
        ParityShardCount = frame.ParityShardCount;
        int memberCount = _mode == MultipathStreamMode.ErasureCode
            ? DataShardCount + ParityShardCount
            : 1;
        ReservedBytes = GetReservationSize(frame);
        _shards = new byte[memberCount][];
        _present = new bool[memberCount];
        Add(frame);
    }

    internal int DataShardCount { get; }

    internal int ParityShardCount { get; }

    internal MultipathStreamMode Mode => _mode;

    internal long ReservedBytes { get; }

    internal static long GetReservationSize(MultipathReceivedFrame frame) =>
        frame.Mode == MultipathStreamMode.ErasureCode
            ? (long)(frame.DataShardCount + frame.ParityShardCount) * frame.Payload!.Length + frame.LogicalLength
            : frame.Payload!.Length;

    internal bool IsDecodable => _decoded is not null ||
        (_mode == MultipathStreamMode.ErasureCode
            ? _presentCount >= DataShardCount
            : _presentCount != 0);

    internal void Add(MultipathReceivedFrame frame)
    {
        byte[] payload = frame.Payload ?? throw new InvalidDataException("A data group received an empty frame.");
        if (frame.Epoch != _epoch || frame.Mode != _mode || frame.LogicalLength != _logicalLength ||
            frame.DataShardCount != DataShardCount || frame.ParityShardCount != ParityShardCount ||
            frame.ShardIndex >= _shards.Length)
        {
            throw new InvalidDataException("Frames for one logical group contain inconsistent metadata.");
        }

        int shard = frame.ShardIndex;
        if (_present[shard])
        {
            if (!_shards[shard].AsSpan().SequenceEqual(payload))
            {
                throw new InvalidDataException("Duplicate shards for one logical group contain different data.");
            }

            return;
        }

        if (_mode == MultipathStreamMode.ErasureCode &&
            _presentCount > 0 && _shards.First(static item => item is not null).Length != payload.Length)
        {
            throw new InvalidDataException("Erasure shards for one logical group have different sizes.");
        }

        _shards[shard] = payload;
        _present[shard] = true;
        _presentCount++;
    }

    internal byte[] Decode()
    {
        if (_decoded is not null)
        {
            return _decoded;
        }

        if (!IsDecodable)
        {
            throw new InvalidOperationException("The logical group is not decodable yet.");
        }

        if (_mode != MultipathStreamMode.ErasureCode)
        {
            _decoded = _shards[0];
            return _decoded;
        }

        int shardSize = _shards.First(static shard => shard is not null).Length;
        for (int shard = 0; shard < _shards.Length; shard++)
        {
            _shards[shard] ??= new byte[shardSize];
        }

        var codec = new ReedSolomonCodec(DataShardCount, ParityShardCount);
        codec.Reconstruct(_shards, _present, 0, shardSize);
        _decoded = new byte[_logicalLength];
        int outputOffset = 0;
        for (int shard = 0; shard < DataShardCount && outputOffset < _decoded.Length; shard++)
        {
            int copyCount = Math.Min(shardSize, _decoded.Length - outputOffset);
            _shards[shard].AsSpan(0, copyCount).CopyTo(_decoded.AsSpan(outputOffset));
            outputOffset += copyCount;
        }

        return _decoded;
    }
}
