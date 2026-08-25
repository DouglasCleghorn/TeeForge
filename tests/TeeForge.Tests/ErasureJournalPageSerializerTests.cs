using System.Buffers.Binary;
using TeeForge.ErasureCoding.Internal;

namespace TeeForge.Tests;

public class ErasureJournalPageSerializerTests
{
    [Fact]
    public void Prepare_and_commit_pages_round_trip_and_match()
    {
        byte[] payload = CreatePayload();
        ErasureJournalPreparePage expectedPrepare = CreatePrepare(payload);
        ErasureJournalRange[] expectedRanges = CreateRanges();
        var prepareBytes = new byte[ErasureFormatV1.PageSize];

        UInt128 writtenPrepareHash = ErasureJournalPreparePageSerializer.Write(
            expectedPrepare,
            expectedRanges,
            payload,
            ErasureFormatV1.DefaultShardSize,
            prepareBytes);

        Assert.True(ErasureJournalPreparePageSerializer.TryRead(
            prepareBytes,
            payload,
            ErasureFormatV1.DefaultShardSize,
            out ErasureJournalPreparePage actualPrepare,
            out ErasureJournalRange[] actualRanges,
            out UInt128 readPrepareHash));
        Assert.Equal(expectedPrepare, actualPrepare);
        Assert.Equal(expectedRanges, actualRanges);
        Assert.Equal(writtenPrepareHash, readPrepareHash);

        ErasureJournalCommitPage expectedCommit = CreateCommit(expectedPrepare, writtenPrepareHash);
        var commitBytes = new byte[ErasureFormatV1.PageSize];
        UInt128 writtenCommitHash = ErasureJournalCommitPageSerializer.Write(expectedCommit, commitBytes);

        Assert.True(ErasureJournalCommitPageSerializer.TryRead(
            commitBytes,
            out ErasureJournalCommitPage actualCommit,
            out UInt128 readCommitHash));
        Assert.Equal(expectedCommit, actualCommit);
        Assert.Equal(writtenCommitHash, readCommitHash);
        Assert.True(ErasureJournalCommitPageSerializer.MatchesPreparePage(
            actualCommit,
            actualPrepare,
            readPrepareHash));
    }

    [Fact]
    public void Combined_fragment_reader_requires_matching_checksummed_pages_and_owns_payload()
    {
        byte[] payload = CreatePayload();
        ErasureJournalPreparePage prepare = CreatePrepare(payload);
        var prepareBytes = new byte[ErasureFormatV1.PageSize];
        UInt128 prepareHash = ErasureJournalPreparePageSerializer.Write(
            prepare,
            CreateRanges(),
            payload,
            ErasureFormatV1.DefaultShardSize,
            prepareBytes);
        var commitBytes = new byte[ErasureFormatV1.PageSize];
        ErasureJournalCommitPageSerializer.Write(CreateCommit(prepare, prepareHash), commitBytes);

        Assert.True(ErasureJournalFragmentSerializer.TryRead(
            prepareBytes,
            payload,
            commitBytes,
            ErasureFormatV1.DefaultShardSize,
            out ErasureJournalFragment? fragment));
        ErasureJournalFragment actual = Assert.IsType<ErasureJournalFragment>(fragment);
        payload[0] ^= 1;
        Assert.NotEqual(payload[0], actual.LocalPayload[0]);
        Assert.NotNull(actual.CommitPage);

        commitBytes[24] ^= 1;
        Assert.False(ErasureJournalFragmentSerializer.TryRead(
            prepareBytes,
            actual.LocalPayload,
            commitBytes,
            ErasureFormatV1.DefaultShardSize,
            out _));
    }

    [Fact]
    public void Combined_fragment_reader_accepts_an_all_zero_uncommitted_page()
    {
        byte[] payload = CreatePayload();
        ErasureJournalPreparePage prepare = CreatePrepare(payload);
        var prepareBytes = new byte[ErasureFormatV1.PageSize];
        ErasureJournalPreparePageSerializer.Write(
            prepare,
            CreateRanges(),
            payload,
            ErasureFormatV1.DefaultShardSize,
            prepareBytes);

        Assert.True(ErasureJournalFragmentSerializer.TryRead(
            prepareBytes,
            payload,
            new byte[ErasureFormatV1.PageSize],
            ErasureFormatV1.DefaultShardSize,
            out ErasureJournalFragment? fragment));
        Assert.Null(Assert.IsType<ErasureJournalFragment>(fragment).CommitPage);
        Assert.False(ErasureJournalFragmentSerializer.TryRead(
            prepareBytes,
            payload,
            [],
            ErasureFormatV1.DefaultShardSize,
            out _));
    }

    [Fact]
    public void Prepare_page_rejects_payload_corruption_separately_from_page_corruption()
    {
        byte[] payload = CreatePayload();
        ErasureJournalPreparePage prepare = CreatePrepare(payload);
        var page = new byte[ErasureFormatV1.PageSize];
        ErasureJournalPreparePageSerializer.Write(
            prepare,
            CreateRanges(),
            payload,
            ErasureFormatV1.DefaultShardSize,
            page);

        payload[0] ^= 1;
        Assert.False(ErasureJournalPreparePageSerializer.TryRead(
            page,
            payload,
            ErasureFormatV1.DefaultShardSize,
            out _,
            out _,
            out _));

        page[3000] ^= 1;
        Assert.False(ErasureJournalPreparePageSerializer.TryRead(
            page,
            ErasureFormatV1.DefaultShardSize,
            out _,
            out _,
            out _));
    }

    [Fact]
    public void Prepare_page_rejects_unaligned_or_overlapping_ranges()
    {
        byte[] payload = CreatePayload();
        ErasureJournalPreparePage prepare = CreatePrepare(payload);
        ErasureJournalRange[] ranges = CreateRanges();
        ranges[1] = ranges[1] with { ShardOffset = 0 };

        Assert.Throws<ArgumentException>(() =>
            ErasureJournalPreparePageSerializer.Write(
                prepare,
                ranges,
                payload,
                ErasureFormatV1.DefaultShardSize,
                new byte[ErasureFormatV1.PageSize]));
    }

    [Fact]
    public void Prepare_page_reader_treats_overflowing_persisted_ranges_as_invalid()
    {
        byte[] payload = CreatePayload();
        ErasureJournalPreparePage prepare = CreatePrepare(payload);
        var page = new byte[ErasureFormatV1.PageSize];
        ErasureJournalPreparePageSerializer.Write(
            prepare,
            CreateRanges(),
            payload,
            ErasureFormatV1.DefaultShardSize,
            page);
        BinaryPrimitives.WriteUInt32LittleEndian(
            page.AsSpan(ErasureFormatV1.JournalRangeDescriptorOffset),
            uint.MaxValue);
        UInt128 hash = ErasureFormatHash.ComputeWithClearedField(page, 128);
        BinaryPrimitives.WriteUInt128LittleEndian(page.AsSpan(128), hash);

        Assert.False(ErasureJournalPreparePageSerializer.TryRead(
            page,
            ErasureFormatV1.DefaultShardSize,
            out _,
            out _,
            out _));
    }

    [Fact]
    public void Commit_page_rejects_unknown_state_and_corruption()
    {
        byte[] payload = CreatePayload();
        ErasureJournalPreparePage prepare = CreatePrepare(payload);
        ErasureJournalCommitPage commit = CreateCommit(prepare, 123);

        Assert.Throws<ArgumentException>(() =>
            ErasureJournalCommitPageSerializer.Write(
                commit with { State = (ErasureJournalCommitState)3 },
                new byte[ErasureFormatV1.PageSize]));

        var page = new byte[ErasureFormatV1.PageSize];
        ErasureJournalCommitPageSerializer.Write(commit, page);
        page[^1] ^= 0x80;
        Assert.False(ErasureJournalCommitPageSerializer.TryRead(page, out _, out _));
    }

    [Fact]
    public void Serialized_pages_match_the_version_one_golden_vectors()
    {
        byte[] payload = CreatePayload();
        ErasureJournalPreparePage prepare = CreatePrepare(payload);
        var prepareBytes = new byte[ErasureFormatV1.PageSize];
        UInt128 prepareHash = ErasureJournalPreparePageSerializer.Write(
            prepare,
            CreateRanges(),
            payload,
            ErasureFormatV1.DefaultShardSize,
            prepareBytes);
        var commitBytes = new byte[ErasureFormatV1.PageSize];
        ErasureJournalCommitPageSerializer.Write(CreateCommit(prepare, prepareHash), commitBytes);

        const string ExpectedPrepareHeader =
            "54656545434A5052010000105A5AA5A50B000000000000000011223344556677" +
            "8899AABBCCDDEEFF102132435465768798A9BACBDCEDFE0FFFEEDDCCBBAA9988" +
            "776655443322110007000000000000002A000000000000000F0E0D0C0B0A09" +
            "0807060504030201000300020000000200721D02BB39418CC8EA4529A09AD2DD" +
            "3DFDB3B49BC0AE18D1F383D64F6EBF9A4F00011000000000000000000000000000";
        const string ExpectedPrepareRanges =
            "0000000000000100000000000000000000000300000001000000010000000000";
        const string ExpectedCommitPrefix =
            "54656545434A434D01000010010000000B000000000000000011223344556677" +
            "8899AABBCCDDEEFF102132435465768798A9BACBDCEDFE0FFFEEDDCCBBAA9988" +
            "77665544332211002A000000000000000F0E0D0C0B0A09080706050403020100" +
            "0300000000000000FDB3B49BC0AE18D1F383D64F6EBF9A4F721D02BB39418CC8" +
            "EA4529A09AD2DD3DBF9899700D063BA5D4F5AD01556ED01D";
        Assert.Equal(ExpectedPrepareHeader, Convert.ToHexString(prepareBytes[..160]));
        Assert.Equal(ExpectedPrepareRanges, Convert.ToHexString(prepareBytes[256..288]));
        Assert.Equal(ExpectedCommitPrefix, Convert.ToHexString(commitBytes[..152]));
    }

    private static byte[] CreatePayload()
    {
        var payload = new byte[2 * ErasureFormatV1.IntegrityBlockSize];
        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(index * 31 + 7);
        }

        return payload;
    }

    private static ErasureJournalRange[] CreateRanges() =>
    [
        new(0, ErasureFormatV1.IntegrityBlockSize, 0, 0),
        new(3 * ErasureFormatV1.IntegrityBlockSize, ErasureFormatV1.IntegrityBlockSize, ErasureFormatV1.IntegrityBlockSize, 0),
    ];

    private static ErasureJournalPreparePage CreatePrepare(ReadOnlySpan<byte> payload) => new(
        TransactionFlags: 0xA5A55A5A,
        TransactionSequence: 11,
        TransactionId: Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
        SetId: Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"),
        ConfigurationId: Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100"),
        ConfigurationGeneration: 7,
        StripeIndex: 42,
        StripeGenerationId: Guid.Parse("0f0e0d0c-0b0a-0908-0706-050403020100"),
        MemberPosition: 3,
        LocalPayloadLength: (uint)payload.Length,
        LocalPayloadHash: ErasureJournalPreparePageSerializer.ComputeLocalPayloadHash(payload));

    private static ErasureJournalCommitPage CreateCommit(
        in ErasureJournalPreparePage prepare,
        UInt128 prepareHash) => new(
        State: ErasureJournalCommitState.Committed,
        TransactionSequence: prepare.TransactionSequence,
        TransactionId: prepare.TransactionId,
        SetId: prepare.SetId,
        ConfigurationId: prepare.ConfigurationId,
        StripeIndex: prepare.StripeIndex,
        StripeGenerationId: prepare.StripeGenerationId,
        MemberPosition: prepare.MemberPosition,
        PreparePageHash: prepareHash,
        LocalPayloadHash: prepare.LocalPayloadHash);
}
