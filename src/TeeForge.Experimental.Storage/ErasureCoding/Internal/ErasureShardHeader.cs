namespace TeeForge.Experimental.Storage.ErasureCoding.Internal;

internal readonly record struct ErasureShardHeader(
    uint ShardFlags,
    ulong ConfigurationGeneration,
    Guid ConfigurationId,
    ulong StripeIndex,
    ulong TransactionSequence,
    Guid StripeGenerationId,
    ushort MemberPosition,
    uint StoredPayloadLength);
