using TeeForge.ErasureCoding.Internal;

namespace TeeForge.Tests;

public class ErasureJournalRecoveryTests
{
    private const int DataShardCount = 6;
    private const int ParityShardCount = 2;
    private const int MemberCount = DataShardCount + ParityShardCount;
    private static readonly Guid SetId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid ConfigurationId = Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f");
    private static readonly Guid TransactionId = Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100");
    private static readonly Guid StripeGenerationId = Guid.Parse("0f0e0d0c-0b0a-0908-0706-050403020100");

    [Fact]
    public void Scan_groups_a_write_quorum_and_orders_transactions_by_sequence()
    {
        byte[][] codeword = CreateCodeword();
        ErasureJournalFragment[] later = CreateFragments(codeword, sequence: 12, committedPositions: [0, 1, 2, 3, 4, 5]);
        ErasureJournalFragment[] earlier = CreateFragments(codeword, sequence: 11, committedPositions: [0, 1, 2, 3, 4, 5]);

        ErasureJournalScanResult result = Scan(later.Concat(earlier));

        Assert.True(result.IsSuccess);
        Assert.Equal([11UL, 12UL], result.Transactions.Select(static item => item.Identity.TransactionSequence));
        Assert.All(result.Transactions, static item => Assert.Equal(6, item.CommittedFragmentCount));
    }

    [Fact]
    public void Scan_ignores_a_fully_prepared_but_uncommitted_transaction()
    {
        ErasureJournalFragment[] fragments = CreateFragments(
            CreateCodeword(),
            sequence: 11,
            committedPositions: []);

        ErasureJournalScanResult result = Scan(fragments);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Transactions);
    }

    [Fact]
    public void Scan_rejects_commit_evidence_below_write_quorum()
    {
        ErasureJournalFragment[] fragments = CreateFragments(
            CreateCodeword(),
            sequence: 11,
            committedPositions: [0, 1, 2, 3, 4]);

        ErasureJournalScanResult result = Scan(fragments);

        Assert.Equal(ErasureJournalScanState.InsufficientCommitQuorum, result.State);
        Assert.Equal(11UL, result.FailedSequence);
        Assert.Empty(result.Transactions);
    }

    [Fact]
    public void Scan_rejects_conflicting_transaction_identities_at_one_sequence()
    {
        byte[][] codeword = CreateCodeword();
        ErasureJournalFragment[] fragments = CreateFragments(
            codeword,
            sequence: 11,
            committedPositions: [0, 1, 2, 3, 4, 5]);
        ErasureJournalFragment conflicting = CreateFragment(
            memberPosition: 7,
            codeword,
            sequence: 11,
            committed: false,
            transactionId: Guid.Parse("11111111-2222-3333-4444-555555555555"));

        ErasureJournalScanResult result = Scan(fragments.Append(conflicting));

        Assert.Equal(ErasureJournalScanState.ConflictingSequence, result.State);
        Assert.Equal(11UL, result.FailedSequence);
    }

    [Fact]
    public void Scan_rejects_duplicate_member_positions_and_bad_payloads()
    {
        byte[][] codeword = CreateCodeword();
        ErasureJournalFragment[] fragments = CreateFragments(
            codeword,
            sequence: 11,
            committedPositions: [0, 1, 2, 3, 4, 5]);

        ErasureJournalScanResult duplicateResult = Scan(fragments.Append(fragments[0]));
        Assert.Equal(ErasureJournalScanState.DuplicateMemberPosition, duplicateResult.State);

        ErasureJournalFragment source = fragments[0];
        byte[] corruptedPayload = (byte[])source.LocalPayload.Clone();
        corruptedPayload[0] ^= 1;
        var corrupted = new ErasureJournalFragment(
            source.PreparePage,
            source.Ranges,
            corruptedPayload,
            source.PreparePageHash,
            source.CommitPage);
        ErasureJournalScanResult corruptedResult = Scan(fragments.Skip(1).Append(corrupted));
        Assert.Equal(ErasureJournalScanState.InvalidFragment, corruptedResult.State);
    }

    [Fact]
    public void Replay_reconstructs_a_missing_changed_data_fragment_from_parity()
    {
        byte[][] codeword = CreateCodeword();
        ErasureJournalFragment[] allFragments = CreateFragments(
            codeword,
            sequence: 11,
            committedPositions: [1, 2, 3, 4, 5, 6]);
        ErasureJournalScanResult scan = Scan(allFragments.Skip(1));
        ErasureJournalTransaction transaction = Assert.Single(scan.Transactions);
        var source = new TestReplaySource();
        for (int member = 1; member < DataShardCount; member++)
        {
            source.Set(member, ErasureJournalHomeBlockState.Previous, codeword[member]);
        }

        var codec = new ReedSolomonCodec(DataShardCount, ParityShardCount, ReedSolomonAcceleration.Scalar);
        ErasureJournalReplayResult result = ErasureJournalReplayPlanner.CreatePlan(transaction, source, codec);

        Assert.True(result.IsSuccess);
        ErasureJournalReplayPlan plan = Assert.IsType<ErasureJournalReplayPlan>(result.Plan);
        ErasureJournalReplayMemberWrite memberZero = Assert.Single(
            plan.MemberWrites,
            static item => item.MemberPosition == 0);
        Assert.Equal(codeword[0], Assert.Single(memberZero.Blocks).Payload);
        Assert.Equal(MemberCount, plan.MemberWrites.Length);
    }

    [Fact]
    public void Replay_is_idempotent_when_every_home_block_is_current()
    {
        byte[][] codeword = CreateCodeword();
        ErasureJournalTransaction transaction = Assert.Single(Scan(CreateFragments(
            codeword,
            sequence: 11,
            committedPositions: [0, 1, 2, 3, 4, 5])).Transactions);
        var source = new TestReplaySource();
        for (int member = 0; member < MemberCount; member++)
        {
            source.Set(member, ErasureJournalHomeBlockState.Current, codeword[member]);
        }

        ErasureJournalReplayResult result = ErasureJournalReplayPlanner.CreatePlan(
            transaction,
            source,
            new ReedSolomonCodec(DataShardCount, ParityShardCount, ReedSolomonAcceleration.Scalar));

        Assert.True(result.IsSuccess);
        Assert.Empty(Assert.IsType<ErasureJournalReplayPlan>(result.Plan).MemberWrites);
    }

    [Fact]
    public void Replay_rejects_current_home_bytes_that_disagree_with_the_journal()
    {
        byte[][] codeword = CreateCodeword();
        ErasureJournalTransaction transaction = Assert.Single(Scan(CreateFragments(
            codeword,
            sequence: 11,
            committedPositions: [0, 1, 2, 3, 4, 5])).Transactions);
        byte[] corrupt = (byte[])codeword[0].Clone();
        corrupt[0] ^= 1;
        var source = new TestReplaySource();
        source.Set(0, ErasureJournalHomeBlockState.Current, corrupt);

        ErasureJournalReplayResult result = ErasureJournalReplayPlanner.CreatePlan(
            transaction,
            source,
            new ReedSolomonCodec(DataShardCount, ParityShardCount, ReedSolomonAcceleration.Scalar));

        Assert.Equal(ErasureJournalReplayState.InconsistentCodeword, result.State);
        Assert.Equal(0U, result.FailedShardOffset);
    }

    [Fact]
    public void Replay_faults_when_fewer_than_k_final_fragments_are_available()
    {
        byte[][] codeword = CreateCodeword();
        ErasureJournalFragment[] fragments = CreateFragments(
            codeword,
            sequence: 11,
            committedPositions: [0, 1, 2, 3, 4, 5]);
        ErasureJournalTransaction transaction = Assert.Single(Scan(fragments).Transactions);

        ErasureJournalReplayResult result = ErasureJournalReplayPlanner.CreatePlan(
            transaction,
            new TestReplaySource(),
            new ReedSolomonCodec(DataShardCount, ParityShardCount, ReedSolomonAcceleration.Scalar));

        Assert.Equal(ErasureJournalReplayState.InsufficientFragments, result.State);
        Assert.Equal(0U, result.FailedShardOffset);
    }

    [Fact]
    public void Replay_verifies_the_complete_reconstructed_codeword()
    {
        byte[][] codeword = CreateCodeword();
        ErasureJournalFragment[] fragments = CreateFragments(
            codeword,
            sequence: 11,
            committedPositions: [0, 1, 2, 3, 4, 5]);
        ErasureJournalTransaction transaction = Assert.Single(Scan(fragments).Transactions);
        var source = new TestReplaySource();
        for (int member = 1; member < DataShardCount; member++)
        {
            byte[] block = (byte[])codeword[member].Clone();
            if (member == 1)
            {
                block[0] ^= 1;
            }

            source.Set(member, ErasureJournalHomeBlockState.Previous, block);
        }

        ErasureJournalReplayResult result = ErasureJournalReplayPlanner.CreatePlan(
            transaction,
            source,
            new ReedSolomonCodec(DataShardCount, ParityShardCount, ReedSolomonAcceleration.Scalar));

        Assert.Equal(ErasureJournalReplayState.InconsistentCodeword, result.State);
    }

    private static ErasureJournalScanResult Scan(IEnumerable<ErasureJournalFragment> fragments) =>
        ErasureJournalTransactionGrouper.Scan(
            fragments,
            SetId,
            ConfigurationId,
            expectedConfigurationGeneration: 7,
            DataShardCount,
            ParityShardCount,
            ErasureFormatV1.DefaultShardSize);

    private static ErasureJournalFragment[] CreateFragments(
        byte[][] codeword,
        ulong sequence,
        int[] committedPositions)
    {
        var committed = committedPositions.ToHashSet();
        var fragments = new ErasureJournalFragment[MemberCount];
        for (ushort member = 0; member < fragments.Length; member++)
        {
            fragments[member] = CreateFragment(member, codeword, sequence, committed.Contains(member));
        }

        return fragments;
    }

    private static ErasureJournalFragment CreateFragment(
        ushort memberPosition,
        byte[][] codeword,
        ulong sequence,
        bool committed,
        Guid? transactionId = null)
    {
        bool changed = memberPosition == 0 || memberPosition >= DataShardCount;
        byte[] payload = changed ? (byte[])codeword[memberPosition].Clone() : [];
        ErasureJournalRange[] ranges = changed
            ? [new(0, ErasureFormatV1.IntegrityBlockSize, 0, 0)]
            : [];
        UInt128 payloadHash = ErasureJournalPreparePageSerializer.ComputeLocalPayloadHash(payload);
        var prepare = new ErasureJournalPreparePage(
            TransactionFlags: 0x10,
            TransactionSequence: sequence,
            TransactionId: transactionId ?? TransactionId,
            SetId,
            ConfigurationId,
            ConfigurationGeneration: 7,
            StripeIndex: 42,
            StripeGenerationId,
            MemberPosition: memberPosition,
            LocalPayloadLength: (uint)payload.Length,
            LocalPayloadHash: payloadHash);
        UInt128 prepareHash = (UInt128)(0x1000 + memberPosition);
        ErasureJournalCommitPage? commit = committed
            ? new ErasureJournalCommitPage(
                ErasureJournalCommitState.Committed,
                sequence,
                prepare.TransactionId,
                SetId,
                ConfigurationId,
                prepare.StripeIndex,
                StripeGenerationId,
                memberPosition,
                prepareHash,
                payloadHash)
            : null;
        return new ErasureJournalFragment(prepare, ranges, payload, prepareHash, commit);
    }

    private static byte[][] CreateCodeword()
    {
        var shards = new byte[MemberCount][];
        for (int member = 0; member < MemberCount; member++)
        {
            shards[member] = new byte[ErasureFormatV1.IntegrityBlockSize];
        }

        for (int member = 0; member < DataShardCount; member++)
        {
            for (int index = 0; index < shards[member].Length; index++)
            {
                shards[member][index] = (byte)(member * 37 + index * 13 + 5);
            }
        }

        new ReedSolomonCodec(DataShardCount, ParityShardCount, ReedSolomonAcceleration.Scalar)
            .Encode(shards, 0, ErasureFormatV1.IntegrityBlockSize);
        return shards;
    }

    private sealed class TestReplaySource : IErasureJournalReplaySource
    {
        private readonly Dictionary<int, ErasureJournalHomeBlock> _blocks = [];

        internal void Set(int memberPosition, ErasureJournalHomeBlockState state, byte[] payload) =>
            _blocks[memberPosition] = new ErasureJournalHomeBlock(state, payload);

        public ErasureJournalHomeBlock GetHomeBlock(
            in ErasureJournalTransactionIdentity transaction,
            int memberPosition,
            uint shardOffset,
            int length) =>
            _blocks.GetValueOrDefault(memberPosition);
    }
}
