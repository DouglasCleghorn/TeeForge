namespace TeeForge.ErasureCoding.Internal;

internal sealed record ErasureStripeMemberMetadata(
    ErasureShardHeader Header,
    ulong[] Checksums,
    bool IsImplicitZero);

internal sealed record ErasureStripeGeneration(
    ulong TransactionSequence,
    Guid GenerationId,
    ErasureStripeMemberMetadata?[] Members);

internal static class ErasureStripeMetadataReader
{
    internal static async ValueTask<ErasureStripeGeneration?> ReadCurrentAsync(
        ErasureSetMetadata set,
        ulong stripeIndex,
        CancellationToken cancellationToken)
    {
        ErasureStripeMemberMetadata?[] headers = await ReadAllAsync(set, stripeIndex, cancellationToken).ConfigureAwait(false);
        var groups = new Dictionary<(ulong Sequence, Guid Generation), List<int>>();
        for (int member = 0; member < headers.Length; member++)
        {
            ErasureStripeMemberMetadata? metadata = headers[member];
            if (metadata is null)
            {
                continue;
            }

            var key = metadata.IsImplicitZero
                ? (0UL, Guid.Empty)
                : (metadata.Header.TransactionSequence, metadata.Header.StripeGenerationId);
            if (!groups.TryGetValue(key, out List<int>? positions))
            {
                positions = [];
                groups.Add(key, positions);
            }

            positions.Add(member);
        }

        if (groups.Count == 0)
        {
            return null;
        }

        ulong newestSequence = groups.Keys.Max(static key => key.Sequence);
        var newest = groups.Where(pair => pair.Key.Sequence == newestSequence).ToArray();
        if (newest.Length != 1 || newest[0].Value.Count < set.ReadQuorum)
        {
            return null;
        }

        var selected = new ErasureStripeMemberMetadata?[headers.Length];
        foreach (int position in newest[0].Value)
        {
            selected[position] = headers[position];
        }

        return new ErasureStripeGeneration(newestSequence, newest[0].Key.Generation, selected);
    }

    internal static async ValueTask<ErasureStripeMemberMetadata?[]> ReadAllAsync(
        ErasureSetMetadata set,
        ulong stripeIndex,
        CancellationToken cancellationToken)
    {
        var tasks = new Task<ErasureStripeMemberMetadata?>[set.MemberCount];
        for (int position = 0; position < tasks.Length; position++)
        {
            int capturedPosition = position;
            tasks[position] = ReadOneAsync(set, capturedPosition, stripeIndex, cancellationToken);
        }

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    internal static long GetShardRecordOffset(ErasureSetMetadata set, ulong stripeIndex) =>
        checked(set.Layout.DataOffset + (long)stripeIndex * set.Layout.ShardRecordSize);

    internal static async ValueTask<byte[]?> ReadValidatedBlockAsync(
        ErasureSetMetadata set,
        int memberPosition,
        ulong stripeIndex,
        ErasureStripeMemberMetadata metadata,
        uint blockOffset,
        CancellationToken cancellationToken)
    {
        if (metadata.IsImplicitZero)
        {
            return new byte[ErasureFormatV1.IntegrityBlockSize];
        }

        int checksumIndex = checked((int)(blockOffset / ErasureFormatV1.IntegrityBlockSize));
        if ((uint)checksumIndex >= metadata.Checksums.Length)
        {
            return null;
        }

        ErasureMemberDevice? member = set.Members[memberPosition];
        if (member is null || !member.CanRead)
        {
            return null;
        }

        var block = new byte[ErasureFormatV1.IntegrityBlockSize];
        long offset = checked(
            GetShardRecordOffset(set, stripeIndex) + ErasureFormatV1.ShardHeaderSize + blockOffset);
        try
        {
            await member.ReadExactlyAtAsync(block, offset, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsMemberIoFailure(exception))
        {
            member.Condition = ErasureMemberDeviceCondition.Missing;
            return null;
        }

        if (System.IO.Hashing.XxHash64.HashToUInt64(block) != metadata.Checksums[checksumIndex])
        {
            member.Condition = ErasureMemberDeviceCondition.Corrupt;
            return null;
        }

        return block;
    }

    private static async Task<ErasureStripeMemberMetadata?> ReadOneAsync(
        ErasureSetMetadata set,
        int memberPosition,
        ulong stripeIndex,
        CancellationToken cancellationToken)
    {
        ErasureMemberDevice? member = set.Members[memberPosition];
        if (member is null || !member.CanRead)
        {
            return null;
        }

        var page = new byte[ErasureFormatV1.ShardHeaderSize];
        try
        {
            await member.ReadExactlyAtAsync(
                page,
                GetShardRecordOffset(set, stripeIndex),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsMemberIoFailure(exception))
        {
            member.Condition = ErasureMemberDeviceCondition.Missing;
            return null;
        }

        if (!ErasureShardHeaderSerializer.TryRead(
            page,
            out ErasureShardHeader header,
            out ulong[] checksums,
            out bool implicitZero,
            out _))
        {
            member.Condition = ErasureMemberDeviceCondition.Corrupt;
            return null;
        }

        if (!implicitZero &&
            (header.ConfigurationId != set.Configuration.ConfigurationId ||
             header.ConfigurationGeneration != set.Configuration.ConfigurationGeneration ||
             header.StripeIndex != stripeIndex ||
             header.MemberPosition != memberPosition ||
             header.StoredPayloadLength != set.Configuration.ShardSize))
        {
            member.Condition = ErasureMemberDeviceCondition.Corrupt;
            return null;
        }

        return new ErasureStripeMemberMetadata(header, checksums, implicitZero);
    }

    private static bool IsMemberIoFailure(Exception exception) =>
        exception is IOException or NotSupportedException or ObjectDisposedException;
}
