using TeeForge.Experimental.Storage.ErasureCoding.Internal;

namespace TeeForge.Experimental.Storage.Tests;

public class ErasureFormatV1Tests
{
    [Fact]
    public void Magic_values_are_exactly_eight_bytes()
    {
        Assert.Equal([0x54, 0x65, 0x65, 0x45, 0x43, 0x0D, 0x0A, 0x1A], ErasureFormatV1.MemberMagic.ToArray());
        Assert.Equal("TeeECMET"u8.ToArray(), ErasureFormatV1.MetadataMagic.ToArray());
        Assert.Equal("TeeECSHD"u8.ToArray(), ErasureFormatV1.ShardMagic.ToArray());
        Assert.Equal("TeeECJPR"u8.ToArray(), ErasureFormatV1.JournalPrepareMagic.ToArray());
        Assert.Equal("TeeECJCM"u8.ToArray(), ErasureFormatV1.JournalCommitMagic.ToArray());
    }

    [Theory]
    [InlineData(6, 2, 6)]
    [InlineData(4, 4, 5)]
    [InlineData(2, 1, 2)]
    [InlineData(2, 3, 3)]
    public void Write_quorum_combines_decode_and_majority_requirements(int data, int parity, int expected)
    {
        Assert.Equal(data, ErasureFormatV1.CalculateReadQuorum(data, parity));
        Assert.Equal(expected, ErasureFormatV1.CalculateWriteQuorum(data, parity));
    }

    [Fact]
    public void Default_layout_matches_the_documented_geometry()
    {
        ErasureMemberLayout layout = ErasureFormatV1.CalculateLayout(
            ErasureFormatV1.DefaultShardSize,
            stripeCount: 1024);

        Assert.Equal(8192, layout.MetadataOffset);
        Assert.Equal(4L * 1024 * 1024, layout.MetadataLength);
        Assert.Equal(layout.MetadataOffset + layout.MetadataLength, layout.JournalOffset);
        Assert.Equal(ErasureFormatV1.DefaultShardSize + 8192, layout.JournalSlotSize);
        Assert.Equal(layout.JournalSlotSize * ErasureFormatV1.DefaultJournalSlotCount, layout.JournalLength);
        Assert.Equal(0, layout.DataOffset % ErasureFormatV1.DefaultShardSize);
        Assert.Equal(ErasureFormatV1.DefaultShardSize + 4096, layout.ShardRecordSize);
        Assert.Equal(layout.DataOffset + (1024 * layout.ShardRecordSize), layout.RequiredMemberLength);
    }

    [Theory]
    [InlineData(65535)]
    [InlineData(131072 - 1)]
    [InlineData(16 * 1024 * 1024 + 1)]
    public void Shard_size_must_be_a_supported_power_of_two(int shardSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ErasureFormatV1.CalculateLayout(shardSize, 1));
    }

    [Fact]
    public void Geometry_rejects_more_than_255_members()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ErasureFormatV1.CalculateWriteQuorum(254, 2));
    }

    [Fact]
    public void Logical_capacity_uses_checked_stream_length_arithmetic()
    {
        Assert.Equal(6L * 1024 * 1024, ErasureFormatV1.CalculateLogicalCapacity(6, 2, 1024 * 1024, 1));
        Assert.Throws<OverflowException>(() =>
            ErasureFormatV1.CalculateLogicalCapacity(6, 2, 16 * 1024 * 1024, long.MaxValue));
    }
}
