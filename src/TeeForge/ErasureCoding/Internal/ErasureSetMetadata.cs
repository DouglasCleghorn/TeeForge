namespace TeeForge.ErasureCoding.Internal;

internal sealed class ErasureSetMetadata
{
    internal ErasureSetMetadata(
        ErasureStableConfiguration configuration,
        ErasureMemberDescriptor[] descriptors,
        ErasureMemberDevice?[] members,
        ErasureMemberSuperblock?[] superblocks,
        ErasureMemberLayout layout,
        ulong nextTransactionSequence)
    {
        Configuration = configuration;
        Descriptors = descriptors;
        Members = members;
        Superblocks = superblocks;
        Layout = layout;
        NextTransactionSequence = nextTransactionSequence;
    }

    internal ErasureStableConfiguration Configuration { get; }

    internal ErasureMemberDescriptor[] Descriptors { get; }

    internal ErasureMemberDevice?[] Members { get; }

    internal ErasureMemberSuperblock?[] Superblocks { get; }

    internal ErasureMemberLayout Layout { get; }

    internal ulong NextTransactionSequence { get; set; }

    internal int MemberCount => Configuration.DataShardCount + Configuration.ParityShardCount;

    internal int ReadQuorum => Configuration.DataShardCount;

    internal int WriteQuorum => ErasureFormatV1.CalculateWriteQuorum(
        Configuration.DataShardCount,
        Configuration.ParityShardCount);
}
