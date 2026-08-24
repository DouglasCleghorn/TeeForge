using TeeForge.ErasureCoding.Internal;

namespace TeeForge.Tests;

public class ErasureMemberSuperblockSerializerTests
{
    [Fact]
    public void Round_trip_preserves_fields_and_uses_rfc_uuid_byte_order()
    {
        ErasureMemberSuperblock expected = CreateSuperblock();
        var page = new byte[ErasureFormatV1.PageSize];

        ErasureMemberSuperblockSerializer.Write(expected, page);

        Assert.Equal(Convert.FromHexString("00112233445566778899AABBCCDDEEFF"), page[24..40]);
        Assert.True(ErasureMemberSuperblockSerializer.TryRead(page, out ErasureMemberSuperblock actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Any_corruption_invalidates_the_page_hash()
    {
        var page = new byte[ErasureFormatV1.PageSize];
        ErasureMemberSuperblockSerializer.Write(CreateSuperblock(), page);

        page[3000] ^= 0x80;

        Assert.False(ErasureMemberSuperblockSerializer.TryRead(page, out _));
    }

    [Fact]
    public void Selection_prefers_newest_valid_copy_and_falls_back_from_corruption()
    {
        ErasureMemberSuperblock older = CreateSuperblock() with { SuperblockGeneration = 10 };
        ErasureMemberSuperblock newer = older with { SuperblockGeneration = 11 };
        var first = new byte[ErasureFormatV1.PageSize];
        var second = new byte[ErasureFormatV1.PageSize];
        ErasureMemberSuperblockSerializer.Write(older, first);
        ErasureMemberSuperblockSerializer.Write(newer, second);

        Assert.True(ErasureMemberSuperblockSerializer.TrySelect(first, second, out ErasureMemberSuperblock selected));
        Assert.Equal(newer, selected);

        second[192] ^= 1;
        Assert.True(ErasureMemberSuperblockSerializer.TrySelect(first, second, out selected));
        Assert.Equal(older, selected);
    }

    [Fact]
    public void Selection_rejects_conflicting_copies_at_the_same_generation()
    {
        ErasureMemberSuperblock firstValue = CreateSuperblock();
        ErasureMemberSuperblock secondValue = firstValue with { MemberStateFlags = firstValue.MemberStateFlags + 1 };
        var first = new byte[ErasureFormatV1.PageSize];
        var second = new byte[ErasureFormatV1.PageSize];
        ErasureMemberSuperblockSerializer.Write(firstValue, first);
        ErasureMemberSuperblockSerializer.Write(secondValue, second);

        Assert.False(ErasureMemberSuperblockSerializer.TrySelect(first, second, out _));
    }

    [Fact]
    public void Write_rejects_offsets_that_disagree_with_geometry()
    {
        ErasureMemberSuperblock invalid = CreateSuperblock() with { DataOffset = 1234 };

        Assert.Throws<ArgumentException>(() =>
            ErasureMemberSuperblockSerializer.Write(invalid, new byte[ErasureFormatV1.PageSize]));
    }

    [Fact]
    public void Serialized_prefix_matches_the_version_one_golden_vector()
    {
        var page = new byte[ErasureFormatV1.PageSize];
        ErasureMemberSuperblockSerializer.Write(CreateSuperblock(), page);

        const string ExpectedPrefix =
            "54656545430D0A1A010000105A5AA5A50B000000000000000011223344556677" +
            "8899AABBCCDDEEFF102132435465768798A9BACBDCEDFE0FFFEEDDCCBBAA9988" +
            "7766554433221100070000000000000003000600020004000000100000000100" +
            "0004000000000000000000800100000000200000000000000000400000000000" +
            "0020400000000000008040000000000000009000000000000010000000101000" +
            "0020000000000000001000000500000000112233445566778899AABBCCDDEEFF" +
            "03D69CEED8BB4772CB6312630A2E8375";
        Assert.Equal(ExpectedPrefix, Convert.ToHexString(page[..208]));
        Assert.All(page[208..], static value => Assert.Equal((byte)0, value));
    }

    private static ErasureMemberSuperblock CreateSuperblock()
    {
        const ushort dataCount = 6;
        const ushort parityCount = 2;
        const ulong stripeCount = 1024;
        ErasureMemberLayout layout = ErasureFormatV1.CalculateLayout(
            ErasureFormatV1.DefaultShardSize,
            (long)stripeCount);
        long logicalCapacity = ErasureFormatV1.CalculateLogicalCapacity(
            dataCount,
            parityCount,
            ErasureFormatV1.DefaultShardSize,
            (long)stripeCount);

        return new ErasureMemberSuperblock(
            FeatureFlags: 0xA5A55A5A,
            SuperblockGeneration: 11,
            SetId: Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            MemberId: Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"),
            ConfigurationId: Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100"),
            ConfigurationGeneration: 7,
            MemberPosition: 3,
            DataShardCount: dataCount,
            ParityShardCount: parityCount,
            JournalSlotCount: ErasureFormatV1.DefaultJournalSlotCount,
            ShardSize: ErasureFormatV1.DefaultShardSize,
            IntegrityBlockSize: ErasureFormatV1.IntegrityBlockSize,
            StripeCount: stripeCount,
            LogicalCapacity: (ulong)logicalCapacity,
            MetadataOffset: (ulong)layout.MetadataOffset,
            MetadataLength: (ulong)layout.MetadataLength,
            JournalOffset: (ulong)layout.JournalOffset,
            JournalLength: (ulong)layout.JournalLength,
            DataOffset: (ulong)layout.DataOffset,
            ShardHeaderSize: ErasureFormatV1.ShardHeaderSize,
            ShardRecordSize: (uint)layout.ShardRecordSize,
            ConfigurationRecordOffset: ErasureFormatV1.MetadataOffset,
            ConfigurationRecordLength: ErasureFormatV1.PageSize,
            MemberStateFlags: 5,
            ConfigurationRecordHash: ((UInt128)0xFFEEDDCCBBAA9988UL << 64) | 0x7766554433221100UL);
    }
}
