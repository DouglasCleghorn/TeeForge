using System.IO.Hashing;

namespace TeeForge.ErasureCoding.Internal;

internal static class ErasureJournalReplayExecutor
{
    internal static async ValueTask ReplayAsync(
        ErasureSetMetadata set,
        IReadOnlyList<ErasureJournalTransaction> transactions,
        IReedSolomonCodec codec,
        CancellationToken cancellationToken)
    {
        foreach (ErasureJournalTransaction transaction in transactions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReplayTransactionAsync(set, transaction, codec, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask ReplayTransactionAsync(
        ErasureSetMetadata set,
        ErasureJournalTransaction transaction,
        IReedSolomonCodec codec,
        CancellationToken cancellationToken)
    {
        ErasureStripeMemberMetadata?[] headers = await ErasureStripeMetadataReader.ReadAllAsync(
            set,
            transaction.Identity.StripeIndex,
            cancellationToken).ConfigureAwait(false);
        GenerationSelection selection = SelectGenerations(set, transaction, headers);
        if (selection.IsSuperseded)
        {
            await CheckpointAsync(set, transaction, cancellationToken).ConfigureAwait(false);
            return;
        }

        uint[] blockOffsets = ErasureJournalReplayPlanner.GetAffectedBlockOffsets(transaction);
        var source = new BufferedReplaySource();
        foreach (uint blockOffset in blockOffsets)
        {
            for (int member = 0; member < set.MemberCount; member++)
            {
                ErasureStripeMemberMetadata? metadata = headers[member];
                if (metadata is null)
                {
                    continue;
                }

                ErasureJournalHomeBlockState state;
                if (Matches(metadata, transaction.Identity.TransactionSequence, transaction.Identity.StripeGenerationId))
                {
                    state = ErasureJournalHomeBlockState.Current;
                }
                else if (Matches(metadata, selection.PreviousSequence, selection.PreviousGeneration))
                {
                    state = metadata.IsImplicitZero
                        ? ErasureJournalHomeBlockState.ImplicitZero
                        : ErasureJournalHomeBlockState.Previous;
                }
                else
                {
                    continue;
                }

                if (state == ErasureJournalHomeBlockState.ImplicitZero)
                {
                    source.Set(member, blockOffset, new ErasureJournalHomeBlock(state, ReadOnlyMemory<byte>.Empty));
                    continue;
                }

                byte[]? block = await ErasureStripeMetadataReader.ReadValidatedBlockAsync(
                    set,
                    member,
                    transaction.Identity.StripeIndex,
                    metadata,
                    blockOffset,
                    cancellationToken).ConfigureAwait(false);
                if (block is not null)
                {
                    source.Set(member, blockOffset, new ErasureJournalHomeBlock(state, block));
                }
            }
        }

        ErasureJournalReplayResult replay = ErasureJournalReplayPlanner.CreatePlan(transaction, source, codec);
        if (!replay.IsSuccess || replay.Plan is null)
        {
            throw new InvalidDataException(
                $"Journal transaction {transaction.Identity.TransactionSequence} cannot be replayed: {replay.State}.");
        }

        var writePositions = replay.Plan.MemberWrites
            .Select(static write => write.MemberPosition)
            .ToHashSet();
        int alreadyCurrent = headers.Select((metadata, position) => (metadata, position)).Count(item =>
            item.metadata is not null &&
            !writePositions.Contains((ushort)item.position) &&
            Matches(item.metadata, transaction.Identity.TransactionSequence, transaction.Identity.StripeGenerationId));
        int updated = await ApplyMemberWritesAsync(
            set,
            transaction,
            replay.Plan.MemberWrites,
            headers,
            selection,
            cancellationToken).ConfigureAwait(false);
        if (alreadyCurrent + updated < set.WriteQuorum)
        {
            throw new InvalidDataException(
                $"Journal transaction {transaction.Identity.TransactionSequence} could not establish durable home quorum.");
        }

        await CheckpointAsync(set, transaction, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int> ApplyMemberWritesAsync(
        ErasureSetMetadata set,
        ErasureJournalTransaction transaction,
        IReadOnlyList<ErasureJournalReplayMemberWrite> writes,
        ErasureStripeMemberMetadata?[] headers,
        GenerationSelection selection,
        CancellationToken cancellationToken)
    {
        Task<bool>[] tasks = writes.Select(write => ApplyOneAsync(write)).ToArray();
        bool[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Count(static success => success);

        async Task<bool> ApplyOneAsync(ErasureJournalReplayMemberWrite write)
        {
            int memberPosition = write.MemberPosition;
            ErasureMemberDevice? member = set.Members[memberPosition];
            if (member is null || !member.CanWrite)
            {
                return false;
            }

            ErasureStripeMemberMetadata? previous = headers[memberPosition];
            ulong[]? checksums = CreateBaseChecksums(set, previous, selection);
            if (checksums is null)
            {
                member.Condition = ErasureMemberDeviceCondition.Stale;
                return false;
            }

            try
            {
                long recordOffset = ErasureStripeMetadataReader.GetShardRecordOffset(
                    set,
                    transaction.Identity.StripeIndex);
                foreach (ErasureJournalReplayBlockWrite block in write.Blocks)
                {
                    await member.WriteAtAsync(
                        block.Payload,
                        checked(recordOffset + ErasureFormatV1.ShardHeaderSize + block.ShardOffset),
                        cancellationToken).ConfigureAwait(false);
                    checksums[block.ShardOffset / ErasureFormatV1.IntegrityBlockSize] =
                        XxHash64.HashToUInt64(block.Payload);
                    member.AddReconstructionBytes(block.Payload.Length);
                }

                var header = new ErasureShardHeader(
                    ShardFlags: 0,
                    ConfigurationGeneration: set.Configuration.ConfigurationGeneration,
                    ConfigurationId: set.Configuration.ConfigurationId,
                    StripeIndex: transaction.Identity.StripeIndex,
                    TransactionSequence: transaction.Identity.TransactionSequence,
                    StripeGenerationId: transaction.Identity.StripeGenerationId,
                    MemberPosition: write.MemberPosition,
                    StoredPayloadLength: set.Configuration.ShardSize);
                var headerBytes = new byte[ErasureFormatV1.ShardHeaderSize];
                ErasureShardHeaderSerializer.Write(header, checksums, headerBytes);
                await member.WriteAtAsync(headerBytes, recordOffset, cancellationToken).ConfigureAwait(false);
                await member.FlushAsync(cancellationToken).ConfigureAwait(false);
                member.Condition = ErasureMemberDeviceCondition.Online;
                return true;
            }
            catch (Exception exception) when (IsMemberIoFailure(exception))
            {
                member.Condition = ErasureMemberDeviceCondition.Missing;
                return false;
            }
        }
    }

    private static ulong[]? CreateBaseChecksums(
        ErasureSetMetadata set,
        ErasureStripeMemberMetadata? metadata,
        in GenerationSelection selection)
    {
        int count = checked((int)(set.Configuration.ShardSize / ErasureFormatV1.IntegrityBlockSize));
        if (metadata is null)
        {
            return null;
        }

        if (metadata.IsImplicitZero && selection.PreviousSequence == 0 && selection.PreviousGeneration == Guid.Empty)
        {
            ulong zeroHash = XxHash64.HashToUInt64(new byte[ErasureFormatV1.IntegrityBlockSize]);
            return Enumerable.Repeat(zeroHash, count).ToArray();
        }

        if (Matches(metadata, selection.PreviousSequence, selection.PreviousGeneration) ||
            Matches(metadata, selection.TargetSequence, selection.TargetGeneration))
        {
            return (ulong[])metadata.Checksums.Clone();
        }

        return null;
    }

    private static GenerationSelection SelectGenerations(
        ErasureSetMetadata set,
        ErasureJournalTransaction transaction,
        ErasureStripeMemberMetadata?[] headers)
    {
        var groups = new Dictionary<(ulong Sequence, Guid Generation), int>();
        foreach (ErasureStripeMemberMetadata? metadata in headers)
        {
            if (metadata is null)
            {
                continue;
            }

            var key = metadata.IsImplicitZero
                ? (0UL, Guid.Empty)
                : (metadata.Header.TransactionSequence, metadata.Header.StripeGenerationId);
            groups[key] = groups.GetValueOrDefault(key) + 1;
        }

        var target = (transaction.Identity.TransactionSequence, transaction.Identity.StripeGenerationId);
        foreach (((ulong sequence, Guid generation), _) in groups)
        {
            if (sequence == target.TransactionSequence && generation != target.StripeGenerationId)
            {
                throw new InvalidDataException("A stripe contains conflicting generation UUIDs at one transaction sequence.");
            }
        }

        (ulong Sequence, Guid Generation)[] laterKeys = groups.Keys
            .Where(key => key.Sequence > target.TransactionSequence)
            .ToArray();
        if (laterKeys.Length != 0)
        {
            ulong latestSequence = laterKeys.Max(static key => key.Sequence);
            (ulong Sequence, Guid Generation)[] latestKeys = laterKeys
                .Where(key => key.Sequence == latestSequence)
                .ToArray();
            if (latestKeys.Length != 1)
            {
                throw new InvalidDataException("A stripe has conflicting evidence at a later transaction sequence.");
            }

            if (groups[latestKeys[0]] >= set.WriteQuorum)
            {
                return new GenerationSelection(
                    target.TransactionSequence,
                    target.StripeGenerationId,
                    0,
                    Guid.Empty,
                    IsSuperseded: true);
            }

            throw new InvalidDataException("A stripe has ambiguous evidence from a later transaction sequence.");
        }

        (ulong Sequence, Guid Generation)[] previousKeys = groups.Keys
            .Where(key => key.Sequence < target.TransactionSequence)
            .ToArray();
        if (previousKeys.Length == 0)
        {
            return new GenerationSelection(
                target.TransactionSequence,
                target.StripeGenerationId,
                ulong.MaxValue,
                Guid.Empty,
                IsSuperseded: false);
        }

        ulong previousSequence = previousKeys.Max(static key => key.Sequence);
        Guid[] previousGenerations = previousKeys
            .Where(key => key.Sequence == previousSequence)
            .Select(static key => key.Generation)
            .Distinct()
            .ToArray();
        if (previousGenerations.Length != 1)
        {
            throw new InvalidDataException("A stripe contains conflicting previous generations at one sequence.");
        }

        return new GenerationSelection(
            target.TransactionSequence,
            target.StripeGenerationId,
            previousSequence,
            previousGenerations[0],
            IsSuperseded: false);
    }

    private static async ValueTask CheckpointAsync(
        ErasureSetMetadata set,
        ErasureJournalTransaction transaction,
        CancellationToken cancellationToken)
    {
        var tasks = new List<Task>();
        for (int position = 0; position < set.MemberCount; position++)
        {
            ErasureJournalFragment? fragment = transaction.GetFragment(position);
            ErasureMemberDevice? member = set.Members[position];
            if (fragment is null || fragment.JournalSlotIndex < 0 || member is null || !member.CanWrite)
            {
                continue;
            }

            tasks.Add(CheckpointOneAsync(member, fragment));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        async Task CheckpointOneAsync(ErasureMemberDevice member, ErasureJournalFragment fragment)
        {
            ErasureJournalPreparePage prepare = fragment.PreparePage;
            var checkpoint = new ErasureJournalCommitPage(
                ErasureJournalCommitState.Checkpointed,
                prepare.TransactionSequence,
                prepare.TransactionId,
                prepare.SetId,
                prepare.ConfigurationId,
                prepare.StripeIndex,
                prepare.StripeGenerationId,
                prepare.MemberPosition,
                fragment.PreparePageHash,
                prepare.LocalPayloadHash);
            var page = new byte[ErasureFormatV1.PageSize];
            ErasureJournalCommitPageSerializer.Write(checkpoint, page);
            long commitOffset = checked(
                set.Layout.JournalOffset +
                fragment.JournalSlotIndex * set.Layout.JournalSlotSize +
                set.Layout.JournalSlotSize -
                ErasureFormatV1.PageSize);
            try
            {
                await member.WriteAtAsync(page, commitOffset, cancellationToken).ConfigureAwait(false);
                await member.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsMemberIoFailure(exception))
            {
                member.Condition = ErasureMemberDeviceCondition.Missing;
            }
        }
    }

    private static bool Matches(
        ErasureStripeMemberMetadata metadata,
        ulong sequence,
        Guid generation) =>
        metadata.IsImplicitZero
            ? sequence == 0 && generation == Guid.Empty
            : metadata.Header.TransactionSequence == sequence &&
              metadata.Header.StripeGenerationId == generation;

    private static bool IsMemberIoFailure(Exception exception) =>
        exception is IOException or NotSupportedException or ObjectDisposedException;

    private readonly record struct GenerationSelection(
        ulong TargetSequence,
        Guid TargetGeneration,
        ulong PreviousSequence,
        Guid PreviousGeneration,
        bool IsSuperseded);

    private sealed class BufferedReplaySource : IErasureJournalReplaySource
    {
        private readonly Dictionary<(int Member, uint Offset), ErasureJournalHomeBlock> _blocks = [];

        internal void Set(int member, uint offset, in ErasureJournalHomeBlock block) =>
            _blocks[(member, offset)] = block;

        public ErasureJournalHomeBlock GetHomeBlock(
            in ErasureJournalTransactionIdentity transaction,
            int memberPosition,
            uint shardOffset,
            int length) =>
            _blocks.GetValueOrDefault((memberPosition, shardOffset));
    }
}
