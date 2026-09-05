using TeeForge.RandomAccess;

namespace TeeForge.Experimental.Storage.ErasureCoding.Internal;

internal static class ErasureSetFactory
{
    internal static async ValueTask<ErasureSetMetadata> CreateAsync(
        IReadOnlyList<Stream> streams,
        int dataShardCount,
        int parityShardCount,
        long logicalCapacity,
        int shardSize,
        int journalSlotCount,
        int latencySampleRate,
        CancellationToken cancellationToken)
    {
        ValidateCreateMembers(streams, dataShardCount, parityShardCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(logicalCapacity);
        long stripeWidth = checked(dataShardCount * (long)shardSize);
        if (logicalCapacity % stripeWidth != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(logicalCapacity),
                "Logical capacity must be an exact multiple of dataShardCount * shardSize.");
        }

        long stripeCount = logicalCapacity / stripeWidth;
        ErasureMemberLayout layout = ErasureFormatV1.CalculateLayout(
            shardSize,
            stripeCount,
            ErasureFormatV1.DefaultMetadataLength,
            journalSlotCount);
        var memberIds = new Guid[streams.Count];
        var descriptors = new ErasureMemberDescriptor[streams.Count];
        for (ushort position = 0; position < streams.Count; position++)
        {
            Stream stream = streams[position];
            if (stream.Length != 0)
            {
                throw new ArgumentException("Every member must be empty before formatting.", nameof(streams));
            }

            stream.SetLength(layout.RequiredMemberLength);
            Guid memberId = Guid.NewGuid();
            memberIds[position] = memberId;
            descriptors[position] = new ErasureMemberDescriptor(
                memberId,
                position,
                position < dataShardCount ? ErasureMemberRole.Data : ErasureMemberRole.Parity,
                InitialStateFlags: 0,
                FeatureFlags: 0,
                RequiredMemberLength: (ulong)layout.RequiredMemberLength);
        }

        Guid setId = Guid.NewGuid();
        Guid configurationId = Guid.NewGuid();
        var configuration = new ErasureStableConfiguration(
            RecordFlags: ErasureFormatV1.StableConfigurationCriticalFlag,
            MetadataRecordSequence: 1,
            ConfigurationGeneration: 1,
            ConfigurationId: configurationId,
            ParentConfigurationId: Guid.Empty,
            SetId: setId,
            ConfigurationFlags: 0,
            CodecId: ErasureFormatV1.ReedSolomonCodecId,
            DataShardCount: (ushort)dataShardCount,
            ParityShardCount: (ushort)parityShardCount,
            ShardSize: (uint)shardSize,
            IntegrityBlockSize: ErasureFormatV1.IntegrityBlockSize,
            StripeCount: (ulong)stripeCount,
            LogicalCapacity: (ulong)logicalCapacity);
        int configurationLength = ErasureFormatV1.CalculateStableConfigurationRecordLength(streams.Count);
        var configurationBytes = new byte[configurationLength];
        UInt128 configurationHash = ErasureStableConfigurationSerializer.Write(
            configuration,
            descriptors,
            configurationBytes);
        var devices = new ErasureMemberDevice?[streams.Count];
        var superblocks = new ErasureMemberSuperblock?[streams.Count];
        for (ushort position = 0; position < streams.Count; position++)
        {
            devices[position] = new ErasureMemberDevice(
                streams[position],
                memberIds[position],
                position,
                latencySampleRate);
        }

        await ExecuteAllAsync(devices, async device =>
        {
            await device.WriteAtAsync(
                configurationBytes,
                ErasureFormatV1.MetadataOffset,
                cancellationToken).ConfigureAwait(false);
            ErasureMemberSuperblock incomplete = CreateSuperblock(
                configuration,
                device.MemberId,
                device.Position,
                journalSlotCount,
                layout,
                configurationLength,
                configurationHash,
                generation: 1,
                memberStateFlags: 0);
            var page = new byte[ErasureFormatV1.PageSize];
            ErasureMemberSuperblockSerializer.Write(incomplete, page);
            await device.WriteAtAsync(page, 0, cancellationToken).ConfigureAwait(false);
            await device.WriteAtAsync(page, ErasureFormatV1.PageSize, cancellationToken).ConfigureAwait(false);
            await device.FlushAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await ExecuteAllAsync(devices, async device =>
        {
            ErasureMemberSuperblock complete = CreateSuperblock(
                configuration,
                device.MemberId,
                device.Position,
                journalSlotCount,
                layout,
                configurationLength,
                configurationHash,
                generation: 2,
                ErasureFormatV1.MemberStateFormatComplete);
            var page = new byte[ErasureFormatV1.PageSize];
            ErasureMemberSuperblockSerializer.Write(complete, page);
            await device.WriteAtAsync(page, 0, cancellationToken).ConfigureAwait(false);
            await device.FlushAsync(cancellationToken).ConfigureAwait(false);
            await device.WriteAtAsync(page, ErasureFormatV1.PageSize, cancellationToken).ConfigureAwait(false);
            await device.FlushAsync(cancellationToken).ConfigureAwait(false);
            superblocks[device.Position] = complete;
        }).ConfigureAwait(false);

        return new ErasureSetMetadata(
            configuration,
            descriptors,
            devices,
            superblocks,
            layout,
            nextTransactionSequence: 1);
    }

    internal static async ValueTask<ErasureSetMetadata> OpenAsync(
        IReadOnlyList<Stream> streams,
        int latencySampleRate,
        CancellationToken cancellationToken)
    {
        ValidateOpenMembers(streams);
        ParsedMember?[] parsed = await Task.WhenAll(streams.Select(
            (stream, suppliedIndex) => ParseMemberAsync(stream, suppliedIndex, cancellationToken))).ConfigureAwait(false);
        ParsedMember[] valid = parsed.OfType<ParsedMember>().ToArray();
        if (valid.Length == 0)
        {
            throw new InvalidDataException("No supplied stream contains a valid erasure member superblock.");
        }

        Guid[] setIds = valid.Select(static item => item.Superblock.SetId).Distinct().ToArray();
        if (setIds.Length != 1)
        {
            throw new InvalidDataException("Supplied streams belong to different erasure sets.");
        }

        var candidates = new List<Candidate>();
        foreach (IGrouping<ConfigurationKey, ParsedMember> group in valid.GroupBy(
            static item => ConfigurationKey.From(item.Superblock)))
        {
            ParsedMember[] groupMembers = group.ToArray();
            int readQuorum;
            try
            {
                readQuorum = ErasureFormatV1.CalculateReadQuorum(
                    group.Key.DataShardCount,
                    group.Key.ParityShardCount);
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            if (groupMembers.Length < readQuorum ||
                !groupMembers.Any(static item =>
                    (item.Superblock.MemberStateFlags & ErasureFormatV1.MemberStateFormatComplete) != 0))
            {
                continue;
            }

            Candidate? candidate = await ValidateCandidateAsync(group.Key, groupMembers, cancellationToken).ConfigureAwait(false);
            if (candidate is not null && candidate.Members.Length >= readQuorum)
            {
                candidates.Add(candidate);
            }
        }

        if (candidates.Count == 0)
        {
            throw new InvalidDataException("No stable erasure configuration has read quorum.");
        }

        ulong newestGeneration = candidates.Max(static item => item.Configuration.ConfigurationGeneration);
        Candidate[] newest = candidates.Where(item => item.Configuration.ConfigurationGeneration == newestGeneration).ToArray();
        if (newest.Length != 1)
        {
            throw new InvalidDataException("Multiple stable erasure configurations have quorum at the newest generation.");
        }

        Candidate selected = newest[0];
        int memberCount = selected.Configuration.DataShardCount + selected.Configuration.ParityShardCount;
        var devices = new ErasureMemberDevice?[memberCount];
        var superblocks = new ErasureMemberSuperblock?[memberCount];
        foreach (ParsedMember member in selected.Members)
        {
            int position = member.Superblock.MemberPosition;
            if (devices[position] is not null ||
                selected.Descriptors[position].MemberId != member.Superblock.MemberId)
            {
                throw new InvalidDataException("The selected configuration contains duplicate or mismatched member identity.");
            }

            devices[position] = new ErasureMemberDevice(
                member.Stream,
                member.Superblock.MemberId,
                member.Superblock.MemberPosition,
                latencySampleRate);
            superblocks[position] = member.Superblock;
        }

        ErasureMemberSuperblock geometry = selected.Members[0].Superblock;
        var layout = new ErasureMemberLayout(
            (long)geometry.MetadataOffset,
            (long)geometry.MetadataLength,
            (long)geometry.JournalOffset,
            (long)geometry.JournalLength,
            (long)geometry.JournalLength / geometry.JournalSlotCount,
            (long)geometry.DataOffset,
            geometry.ShardRecordSize,
            checked((long)geometry.DataOffset + (long)geometry.StripeCount * geometry.ShardRecordSize));
        return new ErasureSetMetadata(
            selected.Configuration,
            selected.Descriptors,
            devices,
            superblocks,
            layout,
            nextTransactionSequence: 1);
    }

    private static async Task<Candidate?> ValidateCandidateAsync(
        ConfigurationKey key,
        ParsedMember[] members,
        CancellationToken cancellationToken)
    {
        ErasureStableConfiguration? canonicalConfiguration = null;
        ErasureMemberDescriptor[]? canonicalDescriptors = null;
        var accepted = new List<ParsedMember>(members.Length);
        var positions = new HashSet<ushort>();
        var memberIds = new HashSet<Guid>();
        foreach (ParsedMember member in members)
        {
            ErasureMemberSuperblock superblock = member.Superblock;
            if (!GeometryMatches(key, superblock) ||
                !positions.Add(superblock.MemberPosition) ||
                !memberIds.Add(superblock.MemberId) ||
                superblock.ConfigurationRecordLength > int.MaxValue)
            {
                continue;
            }

            var record = new byte[superblock.ConfigurationRecordLength];
            try
            {
                await ReadExactlyAtRawAsync(
                    member.Stream,
                    record,
                    (long)superblock.ConfigurationRecordOffset,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException or ObjectDisposedException)
            {
                continue;
            }

            if (!ErasureStableConfigurationSerializer.TryRead(
                    record,
                    out ErasureStableConfiguration configuration,
                    out ErasureMemberDescriptor[] descriptors,
                    out int recordLength,
                    out UInt128 recordHash) ||
                recordLength != superblock.ConfigurationRecordLength ||
                recordHash != superblock.ConfigurationRecordHash ||
                !ConfigurationMatchesSuperblock(configuration, superblock))
            {
                continue;
            }

            if (canonicalConfiguration is null)
            {
                canonicalConfiguration = configuration;
                canonicalDescriptors = descriptors;
            }
            else if (canonicalConfiguration.Value != configuration ||
                !canonicalDescriptors!.AsSpan().SequenceEqual(descriptors))
            {
                return null;
            }

            accepted.Add(member);
        }

        return canonicalConfiguration is ErasureStableConfiguration selected && canonicalDescriptors is not null
            ? new Candidate(selected, canonicalDescriptors, accepted.ToArray())
            : null;
    }

    private static async Task<ParsedMember?> ParseMemberAsync(
        Stream stream,
        int suppliedIndex,
        CancellationToken cancellationToken)
    {
        var first = new byte[ErasureFormatV1.PageSize];
        var second = new byte[ErasureFormatV1.PageSize];
        try
        {
            await ReadExactlyAtRawAsync(stream, first, 0, cancellationToken).ConfigureAwait(false);
            await ReadExactlyAtRawAsync(stream, second, ErasureFormatV1.PageSize, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ObjectDisposedException)
        {
            return null;
        }

        return ErasureMemberSuperblockSerializer.TrySelect(first, second, out ErasureMemberSuperblock superblock)
            ? new ParsedMember(stream, suppliedIndex, superblock)
            : null;
    }

    private static async ValueTask ReadExactlyAtRawAsync(
        Stream stream,
        Memory<byte> destination,
        long offset,
        CancellationToken cancellationToken)
    {
        if (TeeRandomAccess.TryGet(stream, out ITeeRandomAccessStream? randomAccess) && randomAccess.CanReadAt)
        {
            int total = 0;
            while (total < destination.Length)
            {
                int read = await randomAccess.ReadAtAsync(
                    destination[total..],
                    checked(offset + total),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                total += read;
            }

            return;
        }

        stream.Position = offset;
        await stream.ReadExactlyAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private static ErasureMemberSuperblock CreateSuperblock(
        in ErasureStableConfiguration configuration,
        Guid memberId,
        ushort memberPosition,
        int journalSlotCount,
        in ErasureMemberLayout layout,
        int configurationLength,
        UInt128 configurationHash,
        ulong generation,
        uint memberStateFlags) => new(
        FeatureFlags: 0,
        SuperblockGeneration: generation,
        SetId: configuration.SetId,
        MemberId: memberId,
        ConfigurationId: configuration.ConfigurationId,
        ConfigurationGeneration: configuration.ConfigurationGeneration,
        MemberPosition: memberPosition,
        DataShardCount: configuration.DataShardCount,
        ParityShardCount: configuration.ParityShardCount,
        JournalSlotCount: (ushort)journalSlotCount,
        ShardSize: configuration.ShardSize,
        IntegrityBlockSize: configuration.IntegrityBlockSize,
        StripeCount: configuration.StripeCount,
        LogicalCapacity: configuration.LogicalCapacity,
        MetadataOffset: (ulong)layout.MetadataOffset,
        MetadataLength: (ulong)layout.MetadataLength,
        JournalOffset: (ulong)layout.JournalOffset,
        JournalLength: (ulong)layout.JournalLength,
        DataOffset: (ulong)layout.DataOffset,
        ShardHeaderSize: ErasureFormatV1.ShardHeaderSize,
        ShardRecordSize: (uint)layout.ShardRecordSize,
        ConfigurationRecordOffset: ErasureFormatV1.MetadataOffset,
        ConfigurationRecordLength: (uint)configurationLength,
        MemberStateFlags: memberStateFlags,
        ConfigurationRecordHash: configurationHash);

    private static bool ConfigurationMatchesSuperblock(
        in ErasureStableConfiguration configuration,
        in ErasureMemberSuperblock superblock) =>
        configuration.SetId == superblock.SetId &&
        configuration.ConfigurationId == superblock.ConfigurationId &&
        configuration.ConfigurationGeneration == superblock.ConfigurationGeneration &&
        configuration.DataShardCount == superblock.DataShardCount &&
        configuration.ParityShardCount == superblock.ParityShardCount &&
        configuration.ShardSize == superblock.ShardSize &&
        configuration.IntegrityBlockSize == superblock.IntegrityBlockSize &&
        configuration.StripeCount == superblock.StripeCount &&
        configuration.LogicalCapacity == superblock.LogicalCapacity;

    private static bool GeometryMatches(in ConfigurationKey key, in ErasureMemberSuperblock value) =>
        key == ConfigurationKey.From(value);

    private static async Task ExecuteAllAsync(
        ErasureMemberDevice?[] devices,
        Func<ErasureMemberDevice, Task> operation)
    {
        await Task.WhenAll(devices.Select(device => operation(device!))).ConfigureAwait(false);
    }

    private static void ValidateCreateMembers(
        IReadOnlyList<Stream> streams,
        int dataShardCount,
        int parityShardCount)
    {
        ArgumentNullException.ThrowIfNull(streams);
        int memberCount = checked(dataShardCount + parityShardCount);
        _ = ErasureFormatV1.CalculateReadQuorum(dataShardCount, parityShardCount);
        if (streams.Count != memberCount)
        {
            throw new ArgumentException("Member count must equal dataShardCount + parityShardCount.", nameof(streams));
        }

        var unique = new HashSet<Stream>(ReferenceEqualityComparer.Instance);
        foreach (Stream? stream in streams)
        {
            if (stream is null ||
                !unique.Add(stream) ||
                !stream.CanRead ||
                !stream.CanWrite ||
                !stream.CanSeek)
            {
                throw new ArgumentException("Members must be unique, readable, writable, seekable streams.", nameof(streams));
            }
        }
    }

    private static void ValidateOpenMembers(IReadOnlyList<Stream> streams)
    {
        ArgumentNullException.ThrowIfNull(streams);
        if (streams.Count == 0)
        {
            throw new ArgumentException("At least one member stream is required.", nameof(streams));
        }

        var unique = new HashSet<Stream>(ReferenceEqualityComparer.Instance);
        foreach (Stream? stream in streams)
        {
            if (stream is null || !unique.Add(stream) || !stream.CanRead || !stream.CanSeek)
            {
                throw new ArgumentException("Members must be unique, readable, seekable streams.", nameof(streams));
            }
        }
    }

    private sealed record ParsedMember(Stream Stream, int SuppliedIndex, ErasureMemberSuperblock Superblock);

    private sealed record Candidate(
        ErasureStableConfiguration Configuration,
        ErasureMemberDescriptor[] Descriptors,
        ParsedMember[] Members);

    private readonly record struct ConfigurationKey(
        Guid SetId,
        Guid ConfigurationId,
        ulong ConfigurationGeneration,
        UInt128 ConfigurationRecordHash,
        ushort DataShardCount,
        ushort ParityShardCount,
        ushort JournalSlotCount,
        uint ShardSize,
        uint IntegrityBlockSize,
        ulong StripeCount,
        ulong LogicalCapacity,
        ulong MetadataOffset,
        ulong MetadataLength,
        ulong JournalOffset,
        ulong JournalLength,
        ulong DataOffset,
        uint ShardRecordSize,
        ulong ConfigurationRecordOffset,
        uint ConfigurationRecordLength)
    {
        internal static ConfigurationKey From(in ErasureMemberSuperblock value) => new(
            value.SetId,
            value.ConfigurationId,
            value.ConfigurationGeneration,
            value.ConfigurationRecordHash,
            value.DataShardCount,
            value.ParityShardCount,
            value.JournalSlotCount,
            value.ShardSize,
            value.IntegrityBlockSize,
            value.StripeCount,
            value.LogicalCapacity,
            value.MetadataOffset,
            value.MetadataLength,
            value.JournalOffset,
            value.JournalLength,
            value.DataOffset,
            value.ShardRecordSize,
            value.ConfigurationRecordOffset,
            value.ConfigurationRecordLength);
    }
}
