namespace TeeForge.ErasureCoding.Internal;

internal readonly record struct ErasureStableConfiguration(
    ushort RecordFlags,
    ulong MetadataRecordSequence,
    ulong ConfigurationGeneration,
    Guid ConfigurationId,
    Guid ParentConfigurationId,
    Guid SetId,
    uint ConfigurationFlags,
    ushort CodecId,
    ushort DataShardCount,
    ushort ParityShardCount,
    uint ShardSize,
    uint IntegrityBlockSize,
    ulong StripeCount,
    ulong LogicalCapacity);

internal enum ErasureMemberRole : byte
{
    Data = 0,
    Parity = 1,
}

internal readonly record struct ErasureMemberDescriptor(
    Guid MemberId,
    ushort Position,
    ErasureMemberRole Role,
    byte InitialStateFlags,
    uint FeatureFlags,
    ulong RequiredMemberLength);
