using System.Buffers;
using TeeForge.ErasureCoding;
using TeeForge.ErasureCoding.Internal;
using TeeForge.Experimental.Storage.ErasureCoding.Internal;

namespace TeeForge.Experimental.Storage.ErasureCoding;

/// <summary>Experimental self-describing erasure image with persistent membership and parity maintenance.</summary>
/// <remarks>Not ready for production use. API and format compatibility are not guaranteed.</remarks>
public class ErasureImage : ErasureStream
{
    private readonly ErasureImageOptions _imageOptions;
    private Guid[] _memberIds;
    private Guid _configurationId;
    private ulong _configurationGeneration;

    private ErasureImage(
        MemberAccessor?[] members, Guid[] memberIds, Guid setId,
        Guid configurationId, ulong configurationGeneration,
        int dataShardCount, int parityShardCount, int blockSize,
        long logicalLength, long dataOffset, ErasureImageOptions options)
        : base(members, dataShardCount, parityShardCount, blockSize, logicalLength, dataOffset, options)
    {
        _imageOptions = options;
        _memberIds = memberIds;
        SetId = setId;
        _configurationId = configurationId;
        _configurationGeneration = configurationGeneration;
    }

    /// <summary>Gets the persistent set identifier.</summary>
    public Guid SetId { get; }

    /// <summary>Gets the physical offset of the member payload.</summary>
    public new long DataOffset => base.DataOffset;

    /// <summary>Gets the ordered persistent member identifiers.</summary>
    public IReadOnlyList<Guid> MemberIds => Array.AsReadOnly((Guid[])_memberIds.Clone());

    /// <summary>Creates a new raw or self-describing set over the supplied members.</summary>
    public static ErasureImage Create(
        IReadOnlyList<Stream> members,
        int dataShardCount,
        int parityShardCount,
        long logicalLength,
        int blockSize = ErasureImageOptions.DefaultBlockSize,
        ErasureImageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(members);
        ErasureImageOptions resolved = options ?? ErasureImageOptions.Default;
        ValidateGeometry(dataShardCount, parityShardCount, logicalLength, blockSize);
        int count = checked(dataShardCount + parityShardCount);
        if (members.Count != count)
        {
            throw new ArgumentException($"Exactly {count} member streams are required.", nameof(members));
        }

        if (count > ErasureImageSuperblockSerializer.MaximumMemberCount)
        {
            throw new ArgumentOutOfRangeException(nameof(parityShardCount), "The self-describing member directory is too large.");
        }

        var accessors = new MemberAccessor?[count];
        var seen = new HashSet<Stream>(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < count; index++)
        {
            Stream member = members[index] ?? throw new ArgumentException($"Member {index} is null.", nameof(members));
            if (!seen.Add(member))
            {
                throw new ArgumentException($"Member {index} duplicates another stream object.", nameof(members));
            }

            if (!member.CanWrite)
            {
                throw new ArgumentException($"Member {index} is not writable.", nameof(members));
            }

            accessors[index] = new MemberAccessor(member);
        }

        Guid setId = Guid.NewGuid();
        Guid configurationId = Guid.NewGuid();
        Guid[] memberIds = Enumerable.Range(0, count).Select(static _ => Guid.NewGuid()).ToArray();
        long dataOffset = resolved.Format == ErasureImageFormat.SelfDescribing
            ? AlignUp(2L * ErasureImageSuperblockSerializer.PageSize, blockSize)
            : 0;

        var result = new ErasureImage(
            accessors,
            memberIds,
            setId,
            configurationId,
            1,
            dataShardCount,
            parityShardCount,
            blockSize,
            logicalLength,
            dataOffset,
            resolved);

        try
        {
            result.InitializeMembers();
            return result;
        }
        catch
        {
            result.DisposeMembers(disposeStreams: !resolved.LeaveOpen);
            throw;
        }
    }

    /// <summary>Opens a self-describing set from available members supplied in any order.</summary>
    public static ErasureImage Open(
        IEnumerable<Stream> members,
        ErasureImageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(members);
        ErasureImageOptions resolved = options ?? ErasureImageOptions.Default;
        if (resolved.Format != ErasureImageFormat.SelfDescribing)
        {
            throw new ArgumentException("Use TeeForge.ErasureCoding.ErasureStream.Open for headerless members.", nameof(options));
        }

        Stream[] supplied = members.ToArray();
        if (supplied.Length == 0)
        {
            throw new ArgumentException("At least one member stream is required.", nameof(members));
        }

        var parsed = new List<(Stream Stream, ErasureImageHeader Header)>();
        foreach (Stream member in supplied)
        {
            ArgumentNullException.ThrowIfNull(member);
            parsed.Add((member, ErasureImageHeaderParser.Read(member)));
        }

        ErasureImageHeader basis = parsed[0].Header;
        int configuredCount = basis.MemberCount;
        var ordered = new MemberAccessor?[configuredCount];
        var seenStreams = new HashSet<Stream>(ReferenceEqualityComparer.Instance);
        foreach ((Stream member, ErasureImageHeader header) in parsed)
        {
            if (!seenStreams.Add(member))
            {
                throw new ArgumentException("A member stream was supplied more than once.", nameof(members));
            }

            ValidateCompatibleHeader(basis, header);
            if (ordered[header.MemberPosition] is not null)
            {
                throw new InvalidDataException($"Multiple members claim position {header.MemberPosition}.");
            }

            ordered[header.MemberPosition] = new MemberAccessor(member);
            PositionMemberAtData(member, checked((long)header.DataOffset));
        }

        if (resolved.RequireAllMembers && ordered.Any(static member => member is null))
        {
            throw new InvalidDataException("The self-describing set is missing one or more configured members.");
        }

        if (ordered.Count(static member => member?.CanRead == true) < basis.DataShardCount)
        {
            throw new InvalidDataException("Fewer than the required number of readable members were supplied.");
        }

        return new ErasureImage(
            ordered,
            basis.MemberIds.ToArray(),
            basis.SetId,
            basis.ConfigurationId,
            basis.ConfigurationGeneration,
            basis.DataShardCount,
            basis.ParityShardCount,
            checked((int)basis.BlockSize),
            checked((long)basis.LogicalLength),
            checked((long)basis.DataOffset),
            resolved);
    }
    /// <summary>Adds one trailing parity image without rewriting existing member payloads.</summary>
    public async ValueTask IncreaseParityAsync(
        Stream newParityImage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newParityImage);
        await BeginMaintenanceAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureMaintenanceRandomAccess();
            if (!newParityImage.CanRead || !newParityImage.CanWrite || !newParityImage.CanSeek)
            {
                throw new ArgumentException("A new parity image must be readable, writable, and seekable.", nameof(newParityImage));
            }

            int newParityCount = checked(_parityShardCount + 1);
            if (DataShardCount + newParityCount > ErasureImageSuperblockSerializer.MaximumMemberCount)
            {
                throw new InvalidOperationException("The member directory has reached its maximum size.");
            }

            var target = new MemberAccessor(newParityImage);
            Guid[] newIds = [.. _memberIds, _imageOptions.Format == ErasureImageFormat.SelfDescribing ? Guid.NewGuid() : Guid.Empty];
            var expandedCodec = new ReedSolomonCodec(DataShardCount, newParityCount);
            long codewordCount = GetCodewordCount();
            PrepareTarget(target, codewordCount);

            for (long codeword = 0; codeword < codewordCount; codeword++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CacheEntry entry = AcquireEntry(codeword);
                await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await EnsureAllDataLoadedAsync(entry, cancellationToken).ConfigureAwait(false);
                    byte[][] shards = CreateExpandedShardArray(entry, newParityCount);
                    expandedCodec.Encode(shards, 0, BlockSize);
                    await target.WriteAtAsync(
                        shards[^1].AsMemory(0, BlockSize),
                        checked(DataOffset + codeword * BlockSize),
                        cancellationToken).ConfigureAwait(false);
                    ReturnExpandedParityBuffers(shards, entry.Shards.Length);
                }
                finally
                {
                    entry.Gate.Release();
                    ReleaseEntry(entry);
                }
            }

            await target.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            Guid newConfigurationId = Guid.NewGuid();
            ulong newGeneration = checked(_configurationGeneration + 1);
            MemberAccessor?[] expanded = [.. _members, target];
            if (_imageOptions.Format == ErasureImageFormat.SelfDescribing)
            {
                await WriteConfigurationHeadersAsync(
                    expanded,
                    newIds,
                    newParityCount,
                    newConfigurationId,
                    newGeneration,
                    cancellationToken).ConfigureAwait(false);
            }

            _members = expanded;
            _memberIds = newIds;
            _parityShardCount = newParityCount;
            _configurationId = newConfigurationId;
            _configurationGeneration = newGeneration;
            _codec = expandedCodec;
            ClearCache();
        }
        finally
        {
            EndMaintenance();
        }
    }

    /// <summary>Reduces trailing parity membership and returns the detached streams.</summary>
    public async ValueTask<IReadOnlyList<Stream>> ReduceParityAsync(
        int newParityCount,
        CancellationToken cancellationToken = default)
    {
        await BeginMaintenanceAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureMaintenanceRandomAccess();
            if (newParityCount < 1 || newParityCount >= _parityShardCount)
            {
                throw new ArgumentOutOfRangeException(nameof(newParityCount));
            }

            int retainedCount = checked(DataShardCount + newParityCount);
            MemberAccessor?[] retained = _members[..retainedCount];
            Guid[] retainedIds = _memberIds[..retainedCount];
            Guid newConfigurationId = Guid.NewGuid();
            ulong newGeneration = checked(_configurationGeneration + 1);
            if (_imageOptions.Format == ErasureImageFormat.SelfDescribing)
            {
                await WriteConfigurationHeadersAsync(
                    retained,
                    retainedIds,
                    newParityCount,
                    newConfigurationId,
                    newGeneration,
                    cancellationToken).ConfigureAwait(false);
            }

            Stream[] detached = _members[retainedCount..]
                .Where(static member => member is not null)
                .Select(static member => member!.Stream)
                .ToArray();
            foreach (MemberAccessor? member in _members[retainedCount..])
            {
                member?.Dispose();
            }

            _members = retained;
            _memberIds = retainedIds;
            _parityShardCount = newParityCount;
            _configurationId = newConfigurationId;
            _configurationGeneration = newGeneration;
            _codec = new ReedSolomonCodec(DataShardCount, newParityCount);
            ClearCache();
            return Array.AsReadOnly(detached);
        }
        finally
        {
            EndMaintenance();
        }
    }

    /// <summary>Reconstructs one missing parity image at its existing configured position.</summary>
    public async ValueTask ReplaceParityImageAsync(
        int parityIndex,
        Stream replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        await BeginMaintenanceAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureMaintenanceRandomAccess();
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)parityIndex, (uint)_parityShardCount);
            int position = checked(DataShardCount + parityIndex);
            if (_members[position] is not null)
            {
                throw new InvalidOperationException("The requested parity position is not missing.");
            }

            if (!replacement.CanRead || !replacement.CanWrite || !replacement.CanSeek)
            {
                throw new ArgumentException("A replacement image must be readable, writable, and seekable.", nameof(replacement));
            }

            var target = new MemberAccessor(replacement);
            long codewordCount = GetCodewordCount();
            PrepareTarget(target, codewordCount);
            for (long codeword = 0; codeword < codewordCount; codeword++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CacheEntry entry = AcquireEntry(codeword);
                await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await EnsureAllDataLoadedAsync(entry, cancellationToken).ConfigureAwait(false);
                    EncodeParity(entry);
                    await target.WriteAtAsync(
                        entry.Shards[position]!.AsMemory(0, BlockSize),
                        checked(DataOffset + codeword * BlockSize),
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    entry.Gate.Release();
                    ReleaseEntry(entry);
                }
            }

            await target.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (_imageOptions.Format == ErasureImageFormat.SelfDescribing)
            {
                await WriteMemberHeaderAsync(target, position, cancellationToken).ConfigureAwait(false);
            }

            _members[position] = target;
            ClearCache();
        }
        finally
        {
            EndMaintenance();
        }
    }
    private void InitializeMembers()
    {
        foreach (MemberAccessor member in _members!)
        {
            if (member.Stream.CanSeek)
            {
                member.Stream.SetLength(0);
                member.Stream.Position = 0;
            }
        }

        if (_imageOptions.Format == ErasureImageFormat.SelfDescribing)
        {
            for (int position = 0; position < _members.Length; position++)
            {
                WriteMemberHeader(_members[position]!, position, initial: true);
            }
        }
        else
        {
            foreach (MemberAccessor member in _members!)
            {
                if (member.Stream.CanSeek)
                {
                    member.Stream.Position = 0;
                }
            }
        }
    }

    private void WriteMemberHeader(MemberAccessor member, int position, bool initial)
    {
        ErasureImageHeader header = CreateHeader(position, _memberIds, _parityShardCount, _configurationId, _configurationGeneration);
        byte[] page = new byte[ErasureImageSuperblockSerializer.PageSize];
        ErasureImageSuperblockSerializer.Write(header, page);
        if (initial && !member.Stream.CanSeek)
        {
            member.Stream.Write(page);
            member.Stream.Write(page);
            WriteZeroes(member.Stream, checked(DataOffset - 2L * page.Length));
            return;
        }

        member.WriteAt(page, 0);
        member.WriteAt(page, ErasureImageSuperblockSerializer.PageSize);
        member.Stream.Flush();
        if (member.Stream.CanSeek)
        {
            member.Stream.Position = DataOffset;
        }
    }

    private async ValueTask WriteMemberHeaderAsync(
        MemberAccessor member,
        int position,
        CancellationToken cancellationToken)
    {
        ErasureImageHeader header = CreateHeader(position, _memberIds, _parityShardCount, _configurationId, _configurationGeneration);
        byte[] page = new byte[ErasureImageSuperblockSerializer.PageSize];
        ErasureImageSuperblockSerializer.Write(header, page);
        await member.WriteAtAsync(page, 0, cancellationToken).ConfigureAwait(false);
        await member.WriteAtAsync(page, ErasureImageSuperblockSerializer.PageSize, cancellationToken).ConfigureAwait(false);
        await member.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteConfigurationHeadersAsync(
        MemberAccessor?[] members,
        Guid[] ids,
        int parityCount,
        Guid configurationId,
        ulong generation,
        CancellationToken cancellationToken)
    {
        for (int position = 0; position < members.Length; position++)
        {
            MemberAccessor member = members[position]
                ?? throw new InvalidOperationException("Every resulting configuration member must be present.");
            ErasureImageHeader header = CreateHeader(position, ids, parityCount, configurationId, generation);
            byte[] page = new byte[ErasureImageSuperblockSerializer.PageSize];
            ErasureImageSuperblockSerializer.Write(header, page);
            await member.WriteAtAsync(page, 0, cancellationToken).ConfigureAwait(false);
            await member.WriteAtAsync(page, ErasureImageSuperblockSerializer.PageSize, cancellationToken).ConfigureAwait(false);
        }

        await Task.WhenAll(members.Select(member => member!.Stream.FlushAsync(cancellationToken))).ConfigureAwait(false);
    }

    private ErasureImageHeader CreateHeader(
        int position,
        Guid[] ids,
        int parityCount,
        Guid configurationId,
        ulong generation) =>
        new(
            ErasureImageSuperblockSerializer.MajorVersion,
            ErasureImageSuperblockSerializer.MinorVersion,
            0,
            0,
            SetId,
            configurationId,
            generation,
            ids[position],
            checked((ushort)position),
            checked((ushort)DataShardCount),
            checked((ushort)parityCount),
            ErasureImageSuperblockSerializer.CodecId,
            ErasureImageSuperblockSerializer.LayoutId,
            checked((uint)BlockSize),
            checked((uint)BlockSize),
            checked((ulong)DataOffset),
            checked((ulong)LogicalLength),
            checked((uint)BlockSize),
            Array.AsReadOnly((Guid[])ids.Clone()));

    private static void ValidateCompatibleHeader(ErasureImageHeader basis, ErasureImageHeader candidate)
    {
        if (basis.SetId != candidate.SetId || basis.ConfigurationId != candidate.ConfigurationId ||
            basis.ConfigurationGeneration != candidate.ConfigurationGeneration ||
            basis.DataShardCount != candidate.DataShardCount || basis.ParityShardCount != candidate.ParityShardCount ||
            basis.BlockSize != candidate.BlockSize || basis.DataOffset != candidate.DataOffset ||
            basis.LogicalLength != candidate.LogicalLength || !basis.MemberIds.SequenceEqual(candidate.MemberIds))
        {
            throw new InvalidDataException("The supplied members do not describe one configuration.");
        }
    }

    private static void PositionMemberAtData(Stream member, long dataOffset)
    {
        if (member.CanSeek)
        {
            member.Position = dataOffset;
            return;
        }

        long remaining = dataOffset - ErasureImageSuperblockSerializer.PageSize;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (remaining > 0)
            {
                int count = (int)Math.Min(buffer.Length, remaining);
                int read = member.Read(buffer, 0, count);
                if (read == 0)
                {
                    throw new EndOfStreamException("The member ended before its data offset.");
                }

                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void PrepareTarget(MemberAccessor target, long codewordCount)
    {
        target.Stream.SetLength(checked(DataOffset + codewordCount * BlockSize));
    }

    private byte[][] CreateExpandedShardArray(CacheEntry entry, int newParityCount)
    {
        int count = checked(DataShardCount + newParityCount);
        var shards = new byte[count][];
        for (int index = 0; index < DataShardCount; index++)
        {
            shards[index] = entry.Shards[index]!;
        }

        for (int index = DataShardCount; index < count; index++)
        {
            shards[index] = ArrayPool<byte>.Shared.Rent(BlockSize);
        }

        return shards;
    }

    private void ReturnExpandedParityBuffers(byte[][] shards, int existingCount)
    {
        for (int index = DataShardCount; index < shards.Length; index++)
        {
            ArrayPool<byte>.Shared.Return(shards[index]);
        }
    }

    private void EnsureMaintenanceRandomAccess()
    {
        if (_members.Take(DataShardCount).Any(static member => member?.CanReadAt != true))
        {
            throw new NotSupportedException("Parity maintenance requires random-access data members.");
        }
    }

    private static long AlignUp(long value, int alignment) =>
        checked((value + alignment - 1) & -alignment);

    private static void WriteZeroes(Stream stream, long count)
    {
        byte[] zeroes = new byte[64 * 1024];
        while (count > 0)
        {
            int length = (int)Math.Min(zeroes.Length, count);
            stream.Write(zeroes, 0, length);
            count -= length;
        }
    }

}
