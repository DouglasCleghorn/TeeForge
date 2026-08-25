namespace TeeForge.ErasureCoding.Internal;

internal readonly record struct ErasureJournalRange(
    uint ShardOffset,
    uint Length,
    uint PayloadOffset,
    uint Flags);

internal readonly record struct ErasureJournalPreparePage(
    uint TransactionFlags,
    ulong TransactionSequence,
    Guid TransactionId,
    Guid SetId,
    Guid ConfigurationId,
    ulong ConfigurationGeneration,
    ulong StripeIndex,
    Guid StripeGenerationId,
    ushort MemberPosition,
    uint LocalPayloadLength,
    UInt128 LocalPayloadHash);

internal enum ErasureJournalCommitState : uint
{
    Committed = 1,
    Checkpointed = 2,
}

internal readonly record struct ErasureJournalCommitPage(
    ErasureJournalCommitState State,
    ulong TransactionSequence,
    Guid TransactionId,
    Guid SetId,
    Guid ConfigurationId,
    ulong StripeIndex,
    Guid StripeGenerationId,
    ushort MemberPosition,
    UInt128 PreparePageHash,
    UInt128 LocalPayloadHash);
