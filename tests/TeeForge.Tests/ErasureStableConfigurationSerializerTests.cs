using TeeForge.ErasureCoding.Internal;

namespace TeeForge.Tests;

public class ErasureStableConfigurationSerializerTests
{
    [Fact]
    public void Round_trip_preserves_configuration_and_ordered_member_descriptors()
    {
        ErasureStableConfiguration expected = CreateConfiguration();
        ErasureMemberDescriptor[] expectedMembers = CreateMembers();
        var record = new byte[ErasureFormatV1.PageSize];

        UInt128 writtenHash = ErasureStableConfigurationSerializer.Write(expected, expectedMembers, record);

        Assert.Equal(Convert.FromHexString("102132435465768798A9BACBDCEDFE0F"), record[40..56]);
        Assert.Equal(Convert.FromHexString("000102030405060708090A0B0C0D0E00"), record[256..272]);
        Assert.True(ErasureStableConfigurationSerializer.TryRead(
            record,
            out ErasureStableConfiguration actual,
            out ErasureMemberDescriptor[] actualMembers,
            out int recordLength,
            out UInt128 readHash));
        Assert.Equal(expected, actual);
        Assert.Equal(expectedMembers, actualMembers);
        Assert.Equal(record.Length, recordLength);
        Assert.Equal(writtenHash, readHash);
    }

    [Fact]
    public void Corruption_anywhere_in_the_aligned_record_invalidates_the_hash()
    {
        var record = new byte[ErasureFormatV1.PageSize];
        ErasureStableConfigurationSerializer.Write(CreateConfiguration(), CreateMembers(), record);

        record[^1] ^= 0x80;

        Assert.False(ErasureStableConfigurationSerializer.TryRead(record, out _, out _, out _, out _));
    }

    [Fact]
    public void Write_rejects_duplicate_or_role_inconsistent_members()
    {
        ErasureMemberDescriptor[] members = CreateMembers();
        members[1] = members[1] with { MemberId = members[0].MemberId };
        Assert.Throws<ArgumentException>(() =>
            ErasureStableConfigurationSerializer.Write(
                CreateConfiguration(),
                members,
                new byte[ErasureFormatV1.PageSize]));

        members = CreateMembers();
        members[6] = members[6] with { Role = ErasureMemberRole.Data };
        Assert.Throws<ArgumentException>(() =>
            ErasureStableConfigurationSerializer.Write(
                CreateConfiguration(),
                members,
                new byte[ErasureFormatV1.PageSize]));
    }

    [Fact]
    public void Serialized_header_matches_the_version_one_golden_vector()
    {
        var record = new byte[ErasureFormatV1.PageSize];
        ErasureStableConfigurationSerializer.Write(CreateConfiguration(), CreateMembers(), record);

        const string ExpectedHeader =
            "54656545434D4554010001000010000001000100000140000900000000000000" +
            "0700000000000000102132435465768798A9BACBDCEDFE0FFFEEDDCCBBAA9988" +
            "776655443322110000112233445566778899AABBCCDDEEFF5A5AA5A501000600" +
            "0200080000001000000001000000000000040000000000000000008001000000" +
            "0001000000000000000000000000000099AEA750FEC37E8EACED69ED220EFC2B";
        Assert.Equal(ExpectedHeader, Convert.ToHexString(record[..160]));
    }

    private static ErasureStableConfiguration CreateConfiguration() => new(
        RecordFlags: ErasureFormatV1.StableConfigurationCriticalFlag,
        MetadataRecordSequence: 9,
        ConfigurationGeneration: 7,
        ConfigurationId: Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"),
        ParentConfigurationId: Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100"),
        SetId: Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
        ConfigurationFlags: 0xA5A55A5A,
        CodecId: ErasureFormatV1.ReedSolomonCodecId,
        DataShardCount: 6,
        ParityShardCount: 2,
        ShardSize: ErasureFormatV1.DefaultShardSize,
        IntegrityBlockSize: ErasureFormatV1.IntegrityBlockSize,
        StripeCount: 1024,
        LogicalCapacity: 6UL * 1024 * 1024 * 1024);

    private static ErasureMemberDescriptor[] CreateMembers()
    {
        ErasureMemberLayout layout = ErasureFormatV1.CalculateLayout(
            ErasureFormatV1.DefaultShardSize,
            1024);
        var members = new ErasureMemberDescriptor[8];
        for (ushort position = 0; position < members.Length; position++)
        {
            members[position] = new ErasureMemberDescriptor(
                new Guid($"00010203-0405-0607-0809-0a0b0c0d0e{position:x2}"),
                position,
                position < 6 ? ErasureMemberRole.Data : ErasureMemberRole.Parity,
                InitialStateFlags: (byte)(position + 1),
                FeatureFlags: (uint)(0x1000 + position),
                RequiredMemberLength: (ulong)layout.RequiredMemberLength);
        }

        return members;
    }
}
