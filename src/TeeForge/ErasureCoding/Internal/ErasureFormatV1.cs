namespace TeeForge.ErasureCoding.Internal;

internal static class ErasureFormatV1
{
    internal const ushort Version = 1;
    internal const int PageSize = 4096;
    internal const int SuperblockCount = 2;
    internal const long MetadataOffset = SuperblockCount * PageSize;
    internal const long DefaultMetadataLength = 4L * 1024 * 1024;
    internal const int DefaultJournalSlotCount = 4;
    internal const int MinimumJournalSlotCount = 2;
    internal const int DefaultShardSize = 1024 * 1024;
    internal const int MinimumShardSize = 64 * 1024;
    internal const int MaximumShardSize = 16 * 1024 * 1024;
    internal const int IntegrityBlockSize = 64 * 1024;
    internal const int MaximumMemberCount = 255;
    internal const int ShardHeaderSize = PageSize;
    internal const int JournalEnvelopeSize = PageSize * 2;
    internal const ushort ReedSolomonCodecId = 1;
    internal const ushort StableConfigurationRecordType = 1;
    internal const ushort StableConfigurationRecordVersion = 1;
    internal const ushort StableConfigurationCriticalFlag = 1;
    internal const int StableConfigurationHeaderSize = 256;
    internal const int MemberDescriptorSize = 64;
    internal const int ShardChecksumOffset = 96;
    internal const int ShardTransactionSequenceOffset =
        ShardChecksumOffset + (MaximumShardSize / IntegrityBlockSize * sizeof(ulong));
    internal const int JournalRangeDescriptorOffset = 256;
    internal const int JournalRangeDescriptorSize = 16;
    internal const int MaximumJournalRangeCount = 128;
    internal const uint MemberStateFormatComplete = 1;

    internal static ReadOnlySpan<byte> MemberMagic => "TeeEC\r\n\u001a"u8;

    internal static ReadOnlySpan<byte> MetadataMagic => "TeeECMET"u8;

    internal static ReadOnlySpan<byte> ShardMagic => "TeeECSHD"u8;

    internal static ReadOnlySpan<byte> JournalPrepareMagic => "TeeECJPR"u8;

    internal static ReadOnlySpan<byte> JournalCommitMagic => "TeeECJCM"u8;

    internal static int CalculateReadQuorum(int dataShardCount, int parityShardCount)
    {
        ValidateCounts(dataShardCount, parityShardCount);
        return dataShardCount;
    }

    internal static int CalculateWriteQuorum(int dataShardCount, int parityShardCount)
    {
        ValidateCounts(dataShardCount, parityShardCount);
        int memberCount = checked(dataShardCount + parityShardCount);
        return Math.Max(dataShardCount, (memberCount / 2) + 1);
    }

    internal static ErasureMemberLayout CalculateLayout(
        int shardSize,
        long stripeCount,
        long metadataLength = DefaultMetadataLength,
        int journalSlotCount = DefaultJournalSlotCount)
    {
        ValidateShardSize(shardSize);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stripeCount);

        ArgumentOutOfRangeException.ThrowIfLessThan(metadataLength, PageSize);
        if (metadataLength % PageSize != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(metadataLength));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(journalSlotCount, MinimumJournalSlotCount);

        checked
        {
            long journalOffset = MetadataOffset + metadataLength;
            long journalSlotSize = AlignUp(JournalEnvelopeSize + (long)shardSize, PageSize);
            long journalLength = journalSlotSize * journalSlotCount;
            long dataOffset = AlignUp(journalOffset + journalLength, Math.Max(PageSize, shardSize));
            long shardRecordSize = ShardHeaderSize + (long)shardSize;
            long requiredMemberLength = dataOffset + (stripeCount * shardRecordSize);

            return new ErasureMemberLayout(
                MetadataOffset,
                metadataLength,
                journalOffset,
                journalLength,
                journalSlotSize,
                dataOffset,
                shardRecordSize,
                requiredMemberLength);
        }
    }

    internal static long CalculateLogicalCapacity(int dataShardCount, int parityShardCount, int shardSize, long stripeCount)
    {
        ValidateCounts(dataShardCount, parityShardCount);
        ValidateShardSize(shardSize);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stripeCount);

        return checked(stripeCount * dataShardCount * (long)shardSize);
    }

    internal static int CalculateStableConfigurationRecordLength(int memberCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(memberCount, 3);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(memberCount, MaximumMemberCount);

        return checked((int)AlignUp(
            StableConfigurationHeaderSize + (long)memberCount * MemberDescriptorSize,
            PageSize));
    }

    private static void ValidateCounts(int dataShardCount, int parityShardCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dataShardCount, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(parityShardCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(parityShardCount, MaximumMemberCount - 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dataShardCount, MaximumMemberCount - parityShardCount);
    }

    private static void ValidateShardSize(int shardSize)
    {
        if (shardSize is < MinimumShardSize or > MaximumShardSize || !int.IsPow2(shardSize))
        {
            throw new ArgumentOutOfRangeException(nameof(shardSize));
        }
    }

    private static long AlignUp(long value, long alignment)
    {
        checked
        {
            return (value + alignment - 1) & -alignment;
        }
    }
}

internal readonly record struct ErasureMemberLayout(
    long MetadataOffset,
    long MetadataLength,
    long JournalOffset,
    long JournalLength,
    long JournalSlotSize,
    long DataOffset,
    long ShardRecordSize,
    long RequiredMemberLength);
