using TeeForge.ErasureCoding.Internal;

namespace TeeForge.Tests;

public class ErasureShardHeaderSerializerTests
{
    [Fact]
    public void Round_trip_preserves_fields_checksums_and_uuid_order()
    {
        ErasureShardHeader expected = CreateHeader();
        ulong[] expectedChecksums = CreateChecksums();
        var page = new byte[ErasureFormatV1.PageSize];

        UInt128 writtenHash = ErasureShardHeaderSerializer.Write(expected, expectedChecksums, page);

        Assert.Equal(Convert.FromHexString("102132435465768798A9BACBDCEDFE0F"), page[24..40]);
        Assert.True(ErasureShardHeaderSerializer.TryRead(
            page,
            out ErasureShardHeader actual,
            out ulong[] actualChecksums,
            out bool isImplicitZero,
            out UInt128 readHash));
        Assert.False(isImplicitZero);
        Assert.Equal(expected, actual);
        Assert.Equal(expectedChecksums, actualChecksums);
        Assert.Equal(writtenHash, readHash);
    }

    [Fact]
    public void All_zero_header_is_the_implicit_initial_shard()
    {
        var page = new byte[ErasureFormatV1.PageSize];

        Assert.True(ErasureShardHeaderSerializer.TryRead(page, out _, out ulong[] checksums, out bool implicitZero, out UInt128 hash));
        Assert.True(implicitZero);
        Assert.Empty(checksums);
        Assert.Equal((UInt128)0, hash);
    }

    [Fact]
    public void Corruption_invalidates_an_explicit_header()
    {
        var page = new byte[ErasureFormatV1.PageSize];
        ErasureShardHeaderSerializer.Write(CreateHeader(), CreateChecksums(), page);

        page[3000] ^= 1;

        Assert.False(ErasureShardHeaderSerializer.TryRead(page, out _, out _, out _, out _));
    }

    [Fact]
    public void Write_rejects_a_checksum_count_that_disagrees_with_payload_length()
    {
        ulong[] checksums = CreateChecksums();

        Assert.Throws<ArgumentException>(() =>
            ErasureShardHeaderSerializer.Write(
                CreateHeader(),
                checksums.AsSpan(0, checksums.Length - 1),
                new byte[ErasureFormatV1.PageSize]));
    }

    [Fact]
    public void Write_rejects_a_non_power_of_two_payload_geometry()
    {
        ErasureShardHeader header = CreateHeader() with
        {
            StoredPayloadLength = 3 * ErasureFormatV1.IntegrityBlockSize,
        };

        Assert.Throws<ArgumentException>(() =>
            ErasureShardHeaderSerializer.Write(
                header,
                new ulong[3],
                new byte[ErasureFormatV1.PageSize]));
    }

    [Fact]
    public void Serialized_prefix_matches_the_version_one_golden_vector()
    {
        var page = new byte[ErasureFormatV1.PageSize];
        ErasureShardHeaderSerializer.Write(CreateHeader(), CreateChecksums(), page);

        const string ExpectedPrefix =
            "5465654543534844010000105A5AA5A507000000000000001021324354657687" +
            "98A9BACBDCEDFE0F2A0000000000000000112233445566778899AABBCCDDEEFF" +
            "030010000000010000001000000000003BC114300C52AE718B979A16A8977D3C" +
            "08070605040302010907060504030201";
        Assert.Equal(ExpectedPrefix, Convert.ToHexString(page[..112]));
    }

    private static ErasureShardHeader CreateHeader() => new(
        ShardFlags: 0xA5A55A5A,
        ConfigurationGeneration: 7,
        ConfigurationId: Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"),
        StripeIndex: 42,
        TransactionSequence: 11,
        StripeGenerationId: Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
        MemberPosition: 3,
        StoredPayloadLength: ErasureFormatV1.DefaultShardSize);

    private static ulong[] CreateChecksums() =>
        Enumerable.Range(0, ErasureFormatV1.DefaultShardSize / ErasureFormatV1.IntegrityBlockSize)
            .Select(static index => 0x0102030405060708UL + (ulong)index)
            .ToArray();
}
