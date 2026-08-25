namespace TeeForge.ErasureCoding.Internal;

internal enum ErasureJournalHomeBlockState
{
    Unavailable,
    Current,
    Previous,
    ImplicitZero,
}

internal readonly record struct ErasureJournalHomeBlock(
    ErasureJournalHomeBlockState State,
    ReadOnlyMemory<byte> Payload);

internal interface IErasureJournalReplaySource
{
    ErasureJournalHomeBlock GetHomeBlock(
        in ErasureJournalTransactionIdentity transaction,
        int memberPosition,
        uint shardOffset,
        int length);
}

internal readonly record struct ErasureJournalReplayBlockWrite(
    uint ShardOffset,
    byte[] Payload);

internal sealed record ErasureJournalReplayMemberWrite(
    ushort MemberPosition,
    ErasureJournalReplayBlockWrite[] Blocks);

internal sealed record ErasureJournalReplayPlan(
    ErasureJournalTransactionIdentity Transaction,
    ErasureJournalReplayMemberWrite[] MemberWrites);

internal enum ErasureJournalReplayState
{
    Ready,
    InvalidSource,
    InsufficientFragments,
    InconsistentCodeword,
}

internal sealed record ErasureJournalReplayResult(
    ErasureJournalReplayState State,
    ErasureJournalReplayPlan? Plan,
    uint? FailedShardOffset = null)
{
    internal bool IsSuccess => State == ErasureJournalReplayState.Ready;
}
