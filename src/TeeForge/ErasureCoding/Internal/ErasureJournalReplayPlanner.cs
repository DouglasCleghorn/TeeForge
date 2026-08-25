namespace TeeForge.ErasureCoding.Internal;

internal static class ErasureJournalReplayPlanner
{
    internal static ErasureJournalReplayResult CreatePlan(
        ErasureJournalTransaction transaction,
        IErasureJournalReplaySource homeSource,
        IReedSolomonCodec codec)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(homeSource);
        ArgumentNullException.ThrowIfNull(codec);
        if (codec.DataShardCount != transaction.DataShardCount ||
            codec.ParityShardCount != transaction.ParityShardCount)
        {
            throw new ArgumentException("The Reed-Solomon codec does not match the journal transaction geometry.", nameof(codec));
        }

        uint[] affectedBlockOffsets = GetAffectedBlockOffsets(transaction);
        if (affectedBlockOffsets.Length == 0)
        {
            return Failure(ErasureJournalReplayState.InvalidSource, null);
        }

        int memberCount = transaction.MemberCount;
        int blockSize = ErasureFormatV1.IntegrityBlockSize;
        var writesByMember = new List<ErasureJournalReplayBlockWrite>?[memberCount];
        foreach (uint blockOffset in affectedBlockOffsets)
        {
            var shards = new byte[memberCount][];
            var present = new bool[memberCount];
            var homeCurrent = new bool[memberCount];
            for (int member = 0; member < memberCount; member++)
            {
                shards[member] = new byte[blockSize];
                ErasureJournalFragment? fragment = transaction.GetFragment(member);
                bool hasJournalBlock = TryGetJournalBlock(fragment, blockOffset, blockSize, out ReadOnlySpan<byte> journalBlock);
                ErasureJournalHomeBlock home = homeSource.GetHomeBlock(
                    transaction.Identity,
                    member,
                    blockOffset,
                    blockSize);
                if (!IsValidHomeBlock(home, blockSize))
                {
                    return Failure(ErasureJournalReplayState.InvalidSource, blockOffset);
                }

                bool usablePreviousData = member < transaction.DataShardCount &&
                    !hasJournalBlock &&
                    home.State is ErasureJournalHomeBlockState.Previous or ErasureJournalHomeBlockState.ImplicitZero;

                if (hasJournalBlock)
                {
                    journalBlock.CopyTo(shards[member]);
                    present[member] = true;
                    if (home.State == ErasureJournalHomeBlockState.Current)
                    {
                        if (!home.Payload.Span.SequenceEqual(journalBlock))
                        {
                            return Failure(ErasureJournalReplayState.InconsistentCodeword, blockOffset);
                        }

                        homeCurrent[member] = true;
                    }
                }
                else if (home.State == ErasureJournalHomeBlockState.Current || usablePreviousData)
                {
                    if (home.State != ErasureJournalHomeBlockState.ImplicitZero)
                    {
                        home.Payload.Span.CopyTo(shards[member]);
                    }

                    present[member] = true;
                    homeCurrent[member] = home.State == ErasureJournalHomeBlockState.Current;
                }
            }

            if (CountPresent(present) < transaction.DataShardCount)
            {
                return Failure(ErasureJournalReplayState.InsufficientFragments, blockOffset);
            }

            codec.Reconstruct(shards, present, 0, blockSize);
            if (!IsConsistentCodeword(shards, transaction.DataShardCount, transaction.ParityShardCount, codec))
            {
                return Failure(ErasureJournalReplayState.InconsistentCodeword, blockOffset);
            }

            for (int member = 0; member < memberCount; member++)
            {
                if (homeCurrent[member])
                {
                    continue;
                }

                (writesByMember[member] ??= []).Add(new ErasureJournalReplayBlockWrite(
                    blockOffset,
                    shards[member]));
            }
        }

        var memberWrites = new List<ErasureJournalReplayMemberWrite>(memberCount);
        for (ushort member = 0; member < memberCount; member++)
        {
            if (writesByMember[member] is List<ErasureJournalReplayBlockWrite> blocks)
            {
                memberWrites.Add(new ErasureJournalReplayMemberWrite(member, blocks.ToArray()));
            }
        }

        return new ErasureJournalReplayResult(
            ErasureJournalReplayState.Ready,
            new ErasureJournalReplayPlan(transaction.Identity, memberWrites.ToArray()));
    }

    internal static uint[] GetAffectedBlockOffsets(ErasureJournalTransaction transaction)
    {
        var offsets = new SortedSet<uint>();
        for (int member = 0; member < transaction.MemberCount; member++)
        {
            ErasureJournalFragment? fragment = transaction.GetFragment(member);
            if (fragment is null)
            {
                continue;
            }

            foreach (ErasureJournalRange range in fragment.Ranges)
            {
                uint end = checked(range.ShardOffset + range.Length);
                for (uint offset = range.ShardOffset; offset < end; offset += ErasureFormatV1.IntegrityBlockSize)
                {
                    offsets.Add(offset);
                }
            }
        }

        return offsets.ToArray();
    }

    private static bool TryGetJournalBlock(
        ErasureJournalFragment? fragment,
        uint blockOffset,
        int blockSize,
        out ReadOnlySpan<byte> block)
    {
        if (fragment is not null)
        {
            foreach (ErasureJournalRange range in fragment.Ranges)
            {
                uint rangeEnd = checked(range.ShardOffset + range.Length);
                uint blockEnd = checked(blockOffset + (uint)blockSize);
                if (blockOffset >= range.ShardOffset && blockEnd <= rangeEnd)
                {
                    int payloadOffset = checked((int)(range.PayloadOffset + blockOffset - range.ShardOffset));
                    block = fragment.LocalPayload.AsSpan(payloadOffset, blockSize);
                    return true;
                }
            }
        }

        block = default;
        return false;
    }

    private static bool IsValidHomeBlock(in ErasureJournalHomeBlock home, int blockSize) =>
        home.State switch
        {
            ErasureJournalHomeBlockState.Unavailable => home.Payload.IsEmpty,
            ErasureJournalHomeBlockState.ImplicitZero => home.Payload.IsEmpty,
            ErasureJournalHomeBlockState.Current or ErasureJournalHomeBlockState.Previous =>
                home.Payload.Length == blockSize,
            _ => false,
        };

    private static int CountPresent(ReadOnlySpan<bool> present)
    {
        int count = 0;
        foreach (bool value in present)
        {
            if (value)
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsConsistentCodeword(
        byte[][] shards,
        int dataShardCount,
        int parityShardCount,
        IReedSolomonCodec codec)
    {
        var verification = new byte[dataShardCount + parityShardCount][];
        for (int data = 0; data < dataShardCount; data++)
        {
            verification[data] = shards[data];
        }

        for (int parity = 0; parity < parityShardCount; parity++)
        {
            verification[dataShardCount + parity] = new byte[shards[0].Length];
        }

        codec.Encode(verification, 0, shards[0].Length);
        for (int parity = 0; parity < parityShardCount; parity++)
        {
            int member = dataShardCount + parity;
            if (!verification[member].AsSpan().SequenceEqual(shards[member]))
            {
                return false;
            }
        }

        return true;
    }

    private static ErasureJournalReplayResult Failure(
        ErasureJournalReplayState state,
        uint? shardOffset) => new(state, null, shardOffset);
}
