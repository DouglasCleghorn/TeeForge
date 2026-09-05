namespace TeeForge.Experimental.Storage.ErasureCoding;

/// <summary>Describes one parsed self-describing <see cref="ErasureImage"/> member.</summary>
public sealed class ErasureImageHeader
{
    internal ErasureImageHeader(
        ushort majorVersion,
        ushort minorVersion,
        ulong requiredFeatures,
        ulong compatibleFeatures,
        Guid setId,
        Guid configurationId,
        ulong configurationGeneration,
        Guid memberId,
        ushort memberPosition,
        ushort dataShardCount,
        ushort parityShardCount,
        uint codecId,
        uint layoutId,
        uint blockSize,
        uint memberRecordSize,
        ulong dataOffset,
        ulong logicalLength,
        uint dataAlignment,
        IReadOnlyList<Guid> memberIds)
    {
        MajorVersion = majorVersion;
        MinorVersion = minorVersion;
        RequiredFeatures = requiredFeatures;
        CompatibleFeatures = compatibleFeatures;
        SetId = setId;
        ConfigurationId = configurationId;
        ConfigurationGeneration = configurationGeneration;
        MemberId = memberId;
        MemberPosition = memberPosition;
        DataShardCount = dataShardCount;
        ParityShardCount = parityShardCount;
        CodecId = codecId;
        LayoutId = layoutId;
        BlockSize = blockSize;
        MemberRecordSize = memberRecordSize;
        DataOffset = dataOffset;
        LogicalLength = logicalLength;
        DataAlignment = dataAlignment;
        MemberIds = memberIds;
    }

    /// <summary>Gets the format major version.</summary>
    public ushort MajorVersion { get; }

    /// <summary>Gets the format minor version.</summary>
    public ushort MinorVersion { get; }

    /// <summary>Gets feature bits that an implementation must understand.</summary>
    public ulong RequiredFeatures { get; }

    /// <summary>Gets feature bits that an implementation may ignore.</summary>
    public ulong CompatibleFeatures { get; }

    /// <summary>Gets the immutable erasure-set identifier.</summary>
    public Guid SetId { get; }

    /// <summary>Gets the selected membership-configuration identifier.</summary>
    public Guid ConfigurationId { get; }

    /// <summary>Gets the selected membership-configuration generation.</summary>
    public ulong ConfigurationGeneration { get; }

    /// <summary>Gets this member's identifier.</summary>
    public Guid MemberId { get; }

    /// <summary>Gets this member's position in <see cref="MemberIds"/>.</summary>
    public ushort MemberPosition { get; }

    /// <summary>Gets the systematic data-shard count.</summary>
    public ushort DataShardCount { get; }

    /// <summary>Gets the parity-shard count.</summary>
    public ushort ParityShardCount { get; }

    /// <summary>Gets the total configured member count.</summary>
    public int MemberCount => MemberIds.Count;

    /// <summary>Gets the exact codec convention identifier.</summary>
    public uint CodecId { get; }

    /// <summary>Gets the member-placement layout identifier.</summary>
    public uint LayoutId { get; }

    /// <summary>Gets the payload bytes stored by one member for one codeword block.</summary>
    public uint BlockSize { get; }

    /// <summary>Gets the physical bytes occupied by one member record.</summary>
    public uint MemberRecordSize { get; }

    /// <summary>Gets the aligned physical offset of member block data.</summary>
    public ulong DataOffset { get; }

    /// <summary>Gets the fixed logical stream length.</summary>
    public ulong LogicalLength { get; }

    /// <summary>Gets the promised member-data alignment.</summary>
    public uint DataAlignment { get; }

    /// <summary>Gets every member identifier in codeword-position order.</summary>
    public IReadOnlyList<Guid> MemberIds { get; }
}
