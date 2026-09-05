namespace TeeForge.Experimental.Storage.ErasureCoding.Internal;

internal sealed class ErasureJournalFragment
{
    internal ErasureJournalFragment(
        in ErasureJournalPreparePage preparePage,
        ErasureJournalRange[] ranges,
        byte[] localPayload,
        UInt128 preparePageHash,
        ErasureJournalCommitPage? commitPage = null,
        int journalSlotIndex = -1)
    {
        PreparePage = preparePage;
        Ranges = ranges ?? throw new ArgumentNullException(nameof(ranges));
        LocalPayload = localPayload ?? throw new ArgumentNullException(nameof(localPayload));
        PreparePageHash = preparePageHash;
        CommitPage = commitPage;
        JournalSlotIndex = journalSlotIndex;
    }

    internal ErasureJournalPreparePage PreparePage { get; }

    internal ErasureJournalRange[] Ranges { get; }

    internal byte[] LocalPayload { get; }

    internal UInt128 PreparePageHash { get; }

    internal ErasureJournalCommitPage? CommitPage { get; }

    internal int JournalSlotIndex { get; }
}

internal readonly record struct ErasureJournalTransactionIdentity(
    uint TransactionFlags,
    ulong TransactionSequence,
    Guid TransactionId,
    Guid SetId,
    Guid ConfigurationId,
    ulong ConfigurationGeneration,
    ulong StripeIndex,
    Guid StripeGenerationId);

internal sealed class ErasureJournalTransaction
{
    private readonly ErasureJournalFragment?[] _fragments;

    internal ErasureJournalTransaction(
        in ErasureJournalTransactionIdentity identity,
        ErasureJournalFragment?[] fragments,
        int committedFragmentCount,
        int checkpointedFragmentCount,
        int dataShardCount,
        int parityShardCount,
        int shardSize)
    {
        Identity = identity;
        _fragments = fragments;
        CommittedFragmentCount = committedFragmentCount;
        CheckpointedFragmentCount = checkpointedFragmentCount;
        DataShardCount = dataShardCount;
        ParityShardCount = parityShardCount;
        ShardSize = shardSize;
    }

    internal ErasureJournalTransactionIdentity Identity { get; }

    internal int MemberCount => _fragments.Length;

    internal int CommittedFragmentCount { get; }

    internal int CheckpointedFragmentCount { get; }

    internal int DataShardCount { get; }

    internal int ParityShardCount { get; }

    internal int ShardSize { get; }

    internal ErasureJournalFragment? GetFragment(int memberPosition) => _fragments[memberPosition];
}

internal enum ErasureJournalScanState
{
    Ready,
    ConflictingSequence,
    DuplicateMemberPosition,
    InvalidFragment,
    InsufficientCommitQuorum,
}

internal sealed record ErasureJournalScanResult(
    ErasureJournalScanState State,
    ErasureJournalTransaction[] Transactions,
    ulong? FailedSequence = null)
{
    internal bool IsSuccess => State == ErasureJournalScanState.Ready;
}
