namespace TeeForge.ErasureCoding.Internal;

internal interface IReedSolomonCodec
{
    int DataShardCount { get; }

    int ParityShardCount { get; }

    void Encode(byte[][] shards, int offset, int byteCount);

    void Reconstruct(byte[][] shards, bool[] present, int offset, int byteCount);
}
