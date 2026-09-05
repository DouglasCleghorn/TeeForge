namespace TeeForge.Experimental.Storage.ErasureCoding.Internal;

internal sealed record ErasureJournalSlotScanResult(
    ErasureJournalScanResult Transactions,
    ulong MaximumObservedSequence);

internal static class ErasureJournalSlotScanner
{
    internal static async ValueTask<ErasureJournalSlotScanResult> ScanAsync(
        ErasureSetMetadata set,
        CancellationToken cancellationToken)
    {
        var fragments = new List<ErasureJournalFragment>();
        ulong maximumSequence = 0;
        for (int memberPosition = 0; memberPosition < set.Members.Length; memberPosition++)
        {
            ErasureMemberDevice? member = set.Members[memberPosition];
            ErasureMemberSuperblock? superblock = set.Superblocks[memberPosition];
            if (member is null || superblock is null || !member.CanRead)
            {
                continue;
            }

            for (int slot = 0; slot < superblock.Value.JournalSlotCount; slot++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long slotOffset = checked(set.Layout.JournalOffset + slot * set.Layout.JournalSlotSize);
                long commitOffset = checked(slotOffset + set.Layout.JournalSlotSize - ErasureFormatV1.PageSize);
                var prepareBytes = new byte[ErasureFormatV1.PageSize];
                var commitBytes = new byte[ErasureFormatV1.PageSize];
                try
                {
                    await member.ReadExactlyAtAsync(prepareBytes, slotOffset, cancellationToken).ConfigureAwait(false);
                    await member.ReadExactlyAtAsync(commitBytes, commitOffset, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsMemberIoFailure(exception))
                {
                    member.Condition = ErasureMemberDeviceCondition.Missing;
                    break;
                }

                bool prepareEmpty = ErasureFormatHash.IsAllZero(prepareBytes);
                bool commitEmpty = ErasureFormatHash.IsAllZero(commitBytes);
                if (prepareEmpty)
                {
                    if (!commitEmpty)
                    {
                        return Invalid(maximumSequence);
                    }

                    continue;
                }

                if (!ErasureJournalPreparePageSerializer.TryRead(
                    prepareBytes,
                    checked((int)set.Configuration.ShardSize),
                    out ErasureJournalPreparePage prepare,
                    out _,
                    out _))
                {
                    if (!commitEmpty)
                    {
                        return Invalid(maximumSequence);
                    }

                    continue;
                }

                maximumSequence = Math.Max(maximumSequence, prepare.TransactionSequence);
                var payload = new byte[prepare.LocalPayloadLength];
                try
                {
                    if (payload.Length != 0)
                    {
                        await member.ReadExactlyAtAsync(
                            payload,
                            slotOffset + ErasureFormatV1.PageSize,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (IsMemberIoFailure(exception))
                {
                    member.Condition = ErasureMemberDeviceCondition.Missing;
                    break;
                }

                if (!ErasureJournalFragmentSerializer.TryRead(
                    prepareBytes,
                    payload,
                    commitBytes,
                    checked((int)set.Configuration.ShardSize),
                    out ErasureJournalFragment? fragment,
                    slot))
                {
                    if (!commitEmpty)
                    {
                        return Invalid(maximumSequence);
                    }

                    continue;
                }

                fragments.Add(fragment!);
            }
        }

        ErasureJournalScanResult transactions = ErasureJournalTransactionGrouper.Scan(
            fragments,
            set.Configuration.SetId,
            set.Configuration.ConfigurationId,
            set.Configuration.ConfigurationGeneration,
            set.Configuration.DataShardCount,
            set.Configuration.ParityShardCount,
            checked((int)set.Configuration.ShardSize));
        return new ErasureJournalSlotScanResult(transactions, maximumSequence);
    }

    private static ErasureJournalSlotScanResult Invalid(ulong maximumSequence) => new(
        new ErasureJournalScanResult(ErasureJournalScanState.InvalidFragment, []),
        maximumSequence);

    private static bool IsMemberIoFailure(Exception exception) =>
        exception is IOException or NotSupportedException or ObjectDisposedException;
}
