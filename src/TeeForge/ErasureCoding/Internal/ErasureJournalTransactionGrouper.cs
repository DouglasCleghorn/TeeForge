namespace TeeForge.ErasureCoding.Internal;

internal static class ErasureJournalTransactionGrouper
{
    internal static ErasureJournalScanResult Scan(
        IEnumerable<ErasureJournalFragment> fragments,
        Guid expectedSetId,
        Guid expectedConfigurationId,
        ulong expectedConfigurationGeneration,
        int dataShardCount,
        int parityShardCount,
        int shardSize)
    {
        ArgumentNullException.ThrowIfNull(fragments);
        if (expectedSetId == Guid.Empty)
        {
            throw new ArgumentException("The expected erasure-set identifier cannot be empty.", nameof(expectedSetId));
        }

        if (expectedConfigurationId == Guid.Empty)
        {
            throw new ArgumentException("The expected configuration identifier cannot be empty.", nameof(expectedConfigurationId));
        }

        int memberCount = checked(dataShardCount + parityShardCount);
        int writeQuorum = ErasureFormatV1.CalculateWriteQuorum(dataShardCount, parityShardCount);
        _ = ErasureFormatV1.CalculateLayout(shardSize, stripeCount: 1);
        var bySequence = new SortedDictionary<ulong, SequenceGroup>();

        foreach (ErasureJournalFragment fragment in fragments)
        {
            ArgumentNullException.ThrowIfNull(fragment);
            ErasureJournalPreparePage prepare = fragment.PreparePage;
            ulong sequence = prepare.TransactionSequence;
            if (!IsValidFragment(
                fragment,
                expectedSetId,
                expectedConfigurationId,
                expectedConfigurationGeneration,
                memberCount,
                shardSize))
            {
                return Failure(ErasureJournalScanState.InvalidFragment, sequence);
            }

            ErasureJournalTransactionIdentity identity = CreateIdentity(prepare);
            if (!bySequence.TryGetValue(sequence, out SequenceGroup? group))
            {
                group = new SequenceGroup(identity, memberCount);
                bySequence.Add(sequence, group);
            }
            else if (group.Identity != identity)
            {
                return Failure(ErasureJournalScanState.ConflictingSequence, sequence);
            }

            int memberPosition = prepare.MemberPosition;
            if (group.Fragments[memberPosition] is not null)
            {
                return Failure(ErasureJournalScanState.DuplicateMemberPosition, sequence);
            }

            group.Fragments[memberPosition] = fragment;
            if (fragment.CommitPage is ErasureJournalCommitPage commit)
            {
                group.CommittedFragmentCount++;
                if (commit.State == ErasureJournalCommitState.Checkpointed)
                {
                    group.CheckpointedFragmentCount++;
                }
            }
        }

        var transactions = new List<ErasureJournalTransaction>(bySequence.Count);
        foreach ((ulong sequence, SequenceGroup group) in bySequence)
        {
            if (group.CommittedFragmentCount == 0)
            {
                continue;
            }

            if (group.CommittedFragmentCount < writeQuorum)
            {
                return Failure(ErasureJournalScanState.InsufficientCommitQuorum, sequence);
            }

            if (!HasAnyAfterImage(group.Fragments))
            {
                return Failure(ErasureJournalScanState.InvalidFragment, sequence);
            }

            transactions.Add(new ErasureJournalTransaction(
                group.Identity,
                group.Fragments,
                group.CommittedFragmentCount,
                group.CheckpointedFragmentCount,
                dataShardCount,
                parityShardCount,
                shardSize));
        }

        return new ErasureJournalScanResult(ErasureJournalScanState.Ready, transactions.ToArray());
    }

    private static bool IsValidFragment(
        ErasureJournalFragment fragment,
        Guid expectedSetId,
        Guid expectedConfigurationId,
        ulong expectedConfigurationGeneration,
        int memberCount,
        int shardSize)
    {
        ErasureJournalPreparePage prepare = fragment.PreparePage;
        if (prepare.SetId != expectedSetId ||
            prepare.ConfigurationId != expectedConfigurationId ||
            prepare.ConfigurationGeneration != expectedConfigurationGeneration ||
            prepare.MemberPosition >= memberCount ||
            !ErasureJournalPreparePageSerializer.ValidateLocalPayload(prepare, fragment.LocalPayload) ||
            !RangesDescribePayload(fragment.Ranges, prepare.LocalPayloadLength, shardSize))
        {
            return false;
        }

        return fragment.CommitPage is not ErasureJournalCommitPage commit ||
            ErasureJournalCommitPageSerializer.MatchesPreparePage(commit, prepare, fragment.PreparePageHash);
    }

    private static bool RangesDescribePayload(
        ReadOnlySpan<ErasureJournalRange> ranges,
        uint localPayloadLength,
        int shardSize)
    {
        if (ranges.Length > ErasureFormatV1.MaximumJournalRangeCount)
        {
            return false;
        }

        uint expectedPayloadOffset = 0;
        uint previousShardEnd = 0;
        for (int index = 0; index < ranges.Length; index++)
        {
            ErasureJournalRange range = ranges[index];
            uint shardEnd;
            uint payloadEnd;
            try
            {
                shardEnd = checked(range.ShardOffset + range.Length);
                payloadEnd = checked(range.PayloadOffset + range.Length);
            }
            catch (OverflowException)
            {
                return false;
            }

            if (range.Flags != 0 ||
                range.Length == 0 ||
                range.ShardOffset % ErasureFormatV1.IntegrityBlockSize != 0 ||
                range.Length % ErasureFormatV1.IntegrityBlockSize != 0 ||
                range.PayloadOffset != expectedPayloadOffset ||
                (index > 0 && range.ShardOffset < previousShardEnd) ||
                shardEnd > shardSize ||
                payloadEnd > localPayloadLength)
            {
                return false;
            }

            expectedPayloadOffset = payloadEnd;
            previousShardEnd = shardEnd;
        }

        return expectedPayloadOffset == localPayloadLength;
    }

    private static bool HasAnyAfterImage(ReadOnlySpan<ErasureJournalFragment?> fragments)
    {
        foreach (ErasureJournalFragment? fragment in fragments)
        {
            if (fragment is not null && fragment.Ranges.Length != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static ErasureJournalTransactionIdentity CreateIdentity(
        in ErasureJournalPreparePage prepare) => new(
        prepare.TransactionFlags,
        prepare.TransactionSequence,
        prepare.TransactionId,
        prepare.SetId,
        prepare.ConfigurationId,
        prepare.ConfigurationGeneration,
        prepare.StripeIndex,
        prepare.StripeGenerationId);

    private static ErasureJournalScanResult Failure(ErasureJournalScanState state, ulong sequence) =>
        new(state, [], sequence);

    private sealed class SequenceGroup
    {
        internal SequenceGroup(in ErasureJournalTransactionIdentity identity, int memberCount)
        {
            Identity = identity;
            Fragments = new ErasureJournalFragment?[memberCount];
        }

        internal ErasureJournalTransactionIdentity Identity { get; }

        internal ErasureJournalFragment?[] Fragments { get; }

        internal int CommittedFragmentCount { get; set; }

        internal int CheckpointedFragmentCount { get; set; }
    }
}
