using System.Buffers;
using TeeForge.ErasureCoding.Internal;
using TeeForge.RandomAccess;

namespace TeeForge.ErasureCoding;

/// <summary>
/// Presents fixed-size member streams as one systematic Reed-Solomon-coded logical stream.
/// </summary>
/// <remarks>
/// The lightweight stream has no stripe journal. Partial writes are serialized per codeword block,
/// but an interrupted multi-member write may require parity verification before degraded reads.
/// </remarks>
public class ErasureStream : Stream, ITeeRandomAccessStream
{
    private readonly object _cacheSync = new();
    private readonly object _operationSync = new();
    private readonly Dictionary<long, CacheEntry> _cache = [];
    private readonly LinkedList<CacheEntry> _cacheLru = [];
    private readonly SemaphoreSlim _positionGate = new(1, 1);
    private readonly SemaphoreSlim _maintenanceGate = new(1, 1);
    private readonly CancellationTokenSource _prefetchCancellation = new();
    private readonly HashSet<Task> _prefetchTasks = [];
    private readonly ErasureStreamOptions _options;
    private MemberAccessor?[] _members;
    private Guid[] _memberIds;
    private ReedSolomonCodec _codec;
    private Guid _configurationId;
    private ulong _configurationGeneration;
    private int _parityShardCount;
    private long _position;
    private long _cacheBytes;
    private long _forwardReadCodeword;
    private long _forwardWriteCodeword;
    private bool _forwardWriteCompleted;
    private bool _maintenanceActive;
    private int _activeOperations;
    private TaskCompletionSource? _operationsDrained;
    private int _disposed;

    private ErasureStream(
        MemberAccessor?[] members,
        Guid[] memberIds,
        Guid setId,
        Guid configurationId,
        ulong configurationGeneration,
        int dataShardCount,
        int parityShardCount,
        int blockSize,
        long logicalLength,
        long dataOffset,
        ErasureStreamOptions options)
    {
        _members = members;
        _memberIds = memberIds;
        SetId = setId;
        _configurationId = configurationId;
        _configurationGeneration = configurationGeneration;
        DataShardCount = dataShardCount;
        _parityShardCount = parityShardCount;
        BlockSize = blockSize;
        LogicalLength = logicalLength;
        DataOffset = dataOffset;
        _options = options;
        _codec = new ReedSolomonCodec(dataShardCount, parityShardCount);
    }

    /// <summary>Creates a new raw or self-describing set over the supplied members.</summary>
    public static ErasureStream Create(
        IReadOnlyList<Stream> members,
        int dataShardCount,
        int parityShardCount,
        long logicalLength,
        int blockSize = ErasureStreamOptions.DefaultBlockSize,
        ErasureStreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(members);
        ErasureStreamOptions resolved = options ?? ErasureStreamOptions.Default;
        ValidateGeometry(dataShardCount, parityShardCount, logicalLength, blockSize);
        int count = checked(dataShardCount + parityShardCount);
        if (members.Count != count)
        {
            throw new ArgumentException($"Exactly {count} member streams are required.", nameof(members));
        }

        if (count > ErasureStreamSuperblockSerializer.MaximumMemberCount)
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
        long dataOffset = resolved.Format == ErasureStreamFormat.SelfDescribing
            ? AlignUp(2L * ErasureStreamSuperblockSerializer.PageSize, blockSize)
            : 0;

        var result = new ErasureStream(
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
    public static ErasureStream Open(
        IEnumerable<Stream> members,
        ErasureStreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(members);
        ErasureStreamOptions resolved = options ?? ErasureStreamOptions.Default;
        if (resolved.Format != ErasureStreamFormat.SelfDescribing)
        {
            throw new ArgumentException("Use OpenRaw for a raw member layout.", nameof(options));
        }

        Stream[] supplied = members.ToArray();
        if (supplied.Length == 0)
        {
            throw new ArgumentException("At least one member stream is required.", nameof(members));
        }

        var parsed = new List<(Stream Stream, ErasureStreamHeader Header)>();
        foreach (Stream member in supplied)
        {
            ArgumentNullException.ThrowIfNull(member);
            parsed.Add((member, ErasureStreamHeaderParser.Read(member)));
        }

        ErasureStreamHeader basis = parsed[0].Header;
        int configuredCount = basis.MemberCount;
        var ordered = new MemberAccessor?[configuredCount];
        var seenStreams = new HashSet<Stream>(ReferenceEqualityComparer.Instance);
        foreach ((Stream member, ErasureStreamHeader header) in parsed)
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

        return new ErasureStream(
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

    /// <summary>Opens a raw set whose member positions and geometry are supplied externally.</summary>
    public static ErasureStream OpenRaw(
        IReadOnlyList<Stream?> members,
        int dataShardCount,
        int parityShardCount,
        long logicalLength,
        int blockSize = ErasureStreamOptions.DefaultBlockSize,
        ErasureStreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(members);
        ErasureStreamOptions suppliedOptions = options ?? ErasureStreamOptions.Default;
        var resolved = suppliedOptions.Format == ErasureStreamFormat.Raw
            ? suppliedOptions
            : new ErasureStreamOptions(
                ErasureStreamFormat.Raw,
                suppliedOptions.RequireAllMembers,
                suppliedOptions.LeaveOpen,
                suppliedOptions.MaximumCacheBytes,
                suppliedOptions.ReadAheadBlockCount);
        ValidateGeometry(dataShardCount, parityShardCount, logicalLength, blockSize);
        int count = checked(dataShardCount + parityShardCount);
        if (members.Count != count)
        {
            throw new ArgumentException($"Exactly {count} member positions are required.", nameof(members));
        }

        var accessors = new MemberAccessor?[count];
        var seen = new HashSet<Stream>(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < count; index++)
        {
            Stream? member = members[index];
            if (member is null)
            {
                continue;
            }

            if (!seen.Add(member))
            {
                throw new ArgumentException($"Member {index} duplicates another stream object.", nameof(members));
            }

            accessors[index] = new MemberAccessor(member);
        }

        if (resolved.RequireAllMembers && accessors.Any(static member => member is null))
        {
            throw new InvalidDataException("The raw set is missing one or more configured members.");
        }

        if (accessors.Count(static member => member?.CanRead == true) < dataShardCount)
        {
            throw new InvalidDataException("Fewer than the required number of readable members were supplied.");
        }

        return new ErasureStream(
            accessors,
            Enumerable.Range(0, count).Select(static _ => Guid.Empty).ToArray(),
            Guid.Empty,
            Guid.Empty,
            0,
            dataShardCount,
            parityShardCount,
            blockSize,
            logicalLength,
            0,
            resolved);
    }

    /// <summary>Gets the self-describing set identifier, or an empty UUID for raw members.</summary>
    public Guid SetId { get; }

    /// <summary>Gets the number of systematic data members.</summary>
    public int DataShardCount { get; }

    /// <summary>Gets the current number of configured parity members.</summary>
    public int ParityShardCount => Volatile.Read(ref _parityShardCount);

    /// <summary>Gets the payload block size stored by each member.</summary>
    public int BlockSize { get; }

    /// <summary>Gets the physical offset at which member data begins.</summary>
    public long DataOffset { get; }

    /// <summary>Gets the immutable logical stream length.</summary>
    public long LogicalLength { get; }

    /// <summary>Gets configured member identifiers in codeword order; raw members contain empty UUIDs.</summary>
    public IReadOnlyList<Guid> MemberIds => Array.AsReadOnly((Guid[])_memberIds.Clone());

    /// <summary>Gets configured positions that currently have no member stream.</summary>
    public IReadOnlyList<int> MissingMemberPositions =>
        Enumerable.Range(0, _members.Length).Where(index => _members[index] is null).ToArray();

    /// <inheritdoc/>
    public override bool CanRead => !IsDisposed && ReadableMemberCount >= DataShardCount;

    /// <inheritdoc/>
    public override bool CanSeek => !IsDisposed && (CanReadAt || CanWriteAt);

    /// <inheritdoc/>
    public override bool CanWrite => !IsDisposed && _members.All(static member => member?.CanWrite == true);

    /// <inheritdoc/>
    public bool CanReadAt => !IsDisposed && _members.Count(static member => member?.CanReadAt == true) >= DataShardCount;

    /// <inheritdoc/>
    public bool CanWriteAt => !IsDisposed && _members.All(static member => member?.CanWriteAt == true) &&
        _members.Take(DataShardCount).All(static member => member?.CanReadAt == true);

    /// <inheritdoc/>
    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            return LogicalLength;
        }
    }

    /// <inheritdoc/>
    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return Volatile.Read(ref _position);
        }
        set
        {
            ThrowIfDisposed();
            if (!CanSeek)
            {
                throw new NotSupportedException("The available members do not support seeking.");
            }

            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, LogicalLength);

            Volatile.Write(ref _position, value);
        }
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        _positionGate.Wait();
        try
        {
            long position = _position;
            int read;
            if (CanReadAt)
            {
                read = ReadAtCore(buffer, position, streaming: true);
                ScheduleReadAhead(position + read);
            }
            else
            {
                read = ReadForward(buffer, position);
            }

            _position = position + read;
            return read;
        }
        finally
        {
            _positionGate.Release();
        }
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        await _positionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long position = _position;
            int read;
            if (CanReadAt)
            {
                read = await ReadAtAsyncCore(
                    buffer,
                    position,
                    streaming: true,
                    cancellationToken).ConfigureAwait(false);
                ScheduleReadAhead(position + read);
            }
            else
            {
                read = await ReadForwardAsync(buffer, position, cancellationToken).ConfigureAwait(false);
            }

            _position = position + read;
            return read;
        }
        finally
        {
            _positionGate.Release();
        }
    }

    /// <inheritdoc/>
    public int ReadAt(Span<byte> buffer, long offset) => ReadAtCore(buffer, offset, streaming: false);

    private int ReadAtCore(Span<byte> buffer, long offset, bool streaming)
    {
        using OperationLease operation = EnterOperation();
        EnsureCanReadAt();
        int requested = ValidateReadRange(offset, buffer.Length);
        int completed = 0;
        while (completed < requested)
        {
            long logical = offset + completed;
            MapLogicalOffset(logical, out long codeword, out int dataIndex, out int within);
            int count = Math.Min(requested - completed, BlockSize - within);
            bool completedCodeword = logical + count == LogicalLength ||
                (logical + count) % ((long)DataShardCount * BlockSize) == 0;
            CacheEntry entry = AcquireEntry(codeword);
            entry.Gate.Wait();
            try
            {
                EnsureDataShardLoaded(entry, dataIndex);
                entry.Shards[dataIndex]!.AsSpan(within, count).CopyTo(buffer.Slice(completed, count));
            }
            finally
            {
                entry.Gate.Release();
                ReleaseEntry(entry);
            }

            completed += count;
            if (streaming && completedCodeword)
            {
                EvictCompletedSequentialEntry(codeword);
            }
        }

        return completed;
    }

    /// <inheritdoc/>
    public ValueTask<int> ReadAtAsync(
        Memory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default) =>
        ReadAtAsyncCore(buffer, offset, streaming: false, cancellationToken);

    private async ValueTask<int> ReadAtAsyncCore(
        Memory<byte> buffer,
        long offset,
        bool streaming,
        CancellationToken cancellationToken)
    {
        using OperationLease operation = EnterOperation();
        EnsureCanReadAt();
        int requested = ValidateReadRange(offset, buffer.Length);
        int completed = 0;
        while (completed < requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long logical = offset + completed;
            MapLogicalOffset(logical, out long codeword, out int dataIndex, out int within);
            int count = Math.Min(requested - completed, BlockSize - within);
            bool completedCodeword = logical + count == LogicalLength ||
                (logical + count) % ((long)DataShardCount * BlockSize) == 0;
            CacheEntry entry = AcquireEntry(codeword);
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureDataShardLoadedAsync(entry, dataIndex, cancellationToken).ConfigureAwait(false);
                entry.Shards[dataIndex]!.AsMemory(within, count).CopyTo(buffer[completed..]);
            }
            finally
            {
                entry.Gate.Release();
                ReleaseEntry(entry);
            }

            completed += count;
            if (streaming && completedCodeword)
            {
                EvictCompletedSequentialEntry(codeword);
            }
        }

        return completed;
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _positionGate.Wait();
        try
        {
            long position = _position;
            if (CanWriteAt)
            {
                WriteAtCore(buffer, position, streaming: true);
            }
            else
            {
                WriteForward(buffer, position);
            }

            _position = checked(position + buffer.Length);
        }
        finally
        {
            _positionGate.Release();
        }
    }

    /// <inheritdoc/>
    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc/>
    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        await _positionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long position = _position;
            if (CanWriteAt)
            {
                await WriteAtAsyncCore(
                    buffer,
                    position,
                    streaming: true,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await WriteForwardAsync(buffer, position, cancellationToken).ConfigureAwait(false);
            }

            _position = checked(position + buffer.Length);
        }
        finally
        {
            _positionGate.Release();
        }
    }

    /// <inheritdoc/>
    public void WriteAt(ReadOnlySpan<byte> buffer, long offset) => WriteAtCore(buffer, offset, streaming: false);

    private void WriteAtCore(ReadOnlySpan<byte> buffer, long offset, bool streaming)
    {
        using OperationLease operation = EnterOperation();
        EnsureCanWriteAt();
        ValidateWriteRange(offset, buffer.Length);
        int completed = 0;
        while (completed < buffer.Length)
        {
            long logical = offset + completed;
            long codewordBytes = checked((long)DataShardCount * BlockSize);
            long codeword = logical / codewordBytes;
            int withinCodeword = checked((int)(logical % codewordBytes));
            int count = Math.Min(buffer.Length - completed, checked((int)(codewordBytes - withinCodeword)));
            bool fullCodeword = withinCodeword == 0 && count == codewordBytes;
            CacheEntry entry = AcquireEntry(codeword);
            entry.Gate.Wait();
            try
            {
                if (fullCodeword)
                {
                    EnsureEveryShardAllocated(entry);
                }
                else
                {
                    EnsureAllDataLoaded(entry);
                }

                DataShardRange touched = ApplyToData(entry, buffer.Slice(completed, count), withinCodeword);
                EncodeParity(entry);
                WriteChangedMembers(entry, touched);
            }
            finally
            {
                entry.Gate.Release();
                ReleaseEntry(entry);
            }

            completed += count;
            if (streaming && (fullCodeword || logical + count == LogicalLength))
            {
                EvictCompletedSequentialEntry(codeword);
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask WriteAtAsync(
        ReadOnlyMemory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default) =>
        WriteAtAsyncCore(buffer, offset, streaming: false, cancellationToken);

    private async ValueTask WriteAtAsyncCore(
        ReadOnlyMemory<byte> buffer,
        long offset,
        bool streaming,
        CancellationToken cancellationToken)
    {
        using OperationLease operation = EnterOperation();
        EnsureCanWriteAt();
        ValidateWriteRange(offset, buffer.Length);
        int completed = 0;
        while (completed < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long logical = offset + completed;
            long codewordBytes = checked((long)DataShardCount * BlockSize);
            long codeword = logical / codewordBytes;
            int withinCodeword = checked((int)(logical % codewordBytes));
            int count = Math.Min(buffer.Length - completed, checked((int)(codewordBytes - withinCodeword)));
            bool fullCodeword = withinCodeword == 0 && count == codewordBytes;
            CacheEntry entry = AcquireEntry(codeword);
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (fullCodeword)
                {
                    EnsureEveryShardAllocated(entry);
                }
                else
                {
                    await EnsureAllDataLoadedAsync(entry, cancellationToken).ConfigureAwait(false);
                }

                DataShardRange touched = ApplyToData(entry, buffer.Span.Slice(completed, count), withinCodeword);
                EncodeParity(entry);
                await WriteChangedMembersAsync(entry, touched, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                entry.Gate.Release();
                ReleaseEntry(entry);
            }

            completed += count;
            if (streaming && (fullCodeword || logical + count == LogicalLength))
            {
                EvictCompletedSequentialEntry(codeword);
            }
        }
    }

    /// <summary>Seals a forward-only final partial codeword and flushes all members.</summary>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _positionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!CanWriteAt && !_forwardWriteCompleted)
            {
                if (_position != LogicalLength)
                {
                    throw new InvalidOperationException("A forward-only stream must receive its complete declared logical length.");
                }

                if (_forwardWriteCodeword * (long)DataShardCount * BlockSize < LogicalLength)
                {
                    CacheEntry entry = AcquireEntry(_forwardWriteCodeword);
                    await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        EnsureEveryShardAllocated(entry);
                        EncodeParity(entry);
                        await WriteSequentialCodewordAsync(entry, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        entry.Gate.Release();
                        ReleaseEntry(entry);
                    }
                }

                _forwardWriteCompleted = true;
            }

            await FlushMembersAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _positionGate.Release();
        }
    }

    /// <inheritdoc/>
    public override void Flush()
    {
        ThrowIfDisposed();
        foreach (MemberAccessor? member in _members)
        {
            member?.Stream.Flush();
        }
    }

    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        FlushMembersAsync(cancellationToken).AsTask();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        if (!CanSeek)
        {
            throw new NotSupportedException("The available members do not support seeking.");
        }

        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(Position + offset),
            SeekOrigin.End => checked(LogicalLength + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0 || target > LogicalLength)
        {
            throw new IOException("The requested position is outside the logical stream.");
        }

        Position = target;
        return target;
    }

    /// <inheritdoc/>
    public override void SetLength(long value) =>
        throw new NotSupportedException("ErasureStream has a fixed logical length.");

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
            if (DataShardCount + newParityCount > ErasureStreamSuperblockSerializer.MaximumMemberCount)
            {
                throw new InvalidOperationException("The member directory has reached its maximum size.");
            }

            var target = new MemberAccessor(newParityImage);
            Guid[] newIds = [.. _memberIds, _options.Format == ErasureStreamFormat.SelfDescribing ? Guid.NewGuid() : Guid.Empty];
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
            if (_options.Format == ErasureStreamFormat.SelfDescribing)
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
            if (_options.Format == ErasureStreamFormat.SelfDescribing)
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
            if (_options.Format == ErasureStreamFormat.SelfDescribing)
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

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!disposing || Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            base.Dispose(disposing);
            return;
        }

        _prefetchCancellation.Cancel();
        WaitForPrefetch();
        ClearCache();
        _positionGate.Dispose();
        _maintenanceGate.Dispose();
        _prefetchCancellation.Dispose();
        DisposeMembers(disposeStreams: !_options.LeaveOpen);

        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await base.DisposeAsync().ConfigureAwait(false);
            return;
        }

        _prefetchCancellation.Cancel();
        Task[] tasks;
        lock (_cacheSync)
        {
            tasks = _prefetchTasks.ToArray();
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        ClearCache();
        if (!_options.LeaveOpen)
        {
            foreach (MemberAccessor? member in _members)
            {
                if (member is not null)
                {
                    await member.Stream.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        foreach (MemberAccessor? member in _members)
        {
            member?.Dispose();
        }

        _positionGate.Dispose();
        _maintenanceGate.Dispose();
        _prefetchCancellation.Dispose();
        GC.SuppressFinalize(this);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private int ReadableMemberCount => _members.Count(static member => member?.CanRead == true);

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private long GetCodewordCount()
    {
        long bytes = checked((long)DataShardCount * BlockSize);
        return (LogicalLength + bytes - 1) / bytes;
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

        if (_options.Format == ErasureStreamFormat.SelfDescribing)
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
        ErasureStreamHeader header = CreateHeader(position, _memberIds, _parityShardCount, _configurationId, _configurationGeneration);
        byte[] page = new byte[ErasureStreamSuperblockSerializer.PageSize];
        ErasureStreamSuperblockSerializer.Write(header, page);
        if (initial && !member.Stream.CanSeek)
        {
            member.Stream.Write(page);
            member.Stream.Write(page);
            WriteZeroes(member.Stream, checked(DataOffset - 2L * page.Length));
            return;
        }

        member.WriteAt(page, 0);
        member.WriteAt(page, ErasureStreamSuperblockSerializer.PageSize);
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
        ErasureStreamHeader header = CreateHeader(position, _memberIds, _parityShardCount, _configurationId, _configurationGeneration);
        byte[] page = new byte[ErasureStreamSuperblockSerializer.PageSize];
        ErasureStreamSuperblockSerializer.Write(header, page);
        await member.WriteAtAsync(page, 0, cancellationToken).ConfigureAwait(false);
        await member.WriteAtAsync(page, ErasureStreamSuperblockSerializer.PageSize, cancellationToken).ConfigureAwait(false);
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
            ErasureStreamHeader header = CreateHeader(position, ids, parityCount, configurationId, generation);
            byte[] page = new byte[ErasureStreamSuperblockSerializer.PageSize];
            ErasureStreamSuperblockSerializer.Write(header, page);
            await member.WriteAtAsync(page, 0, cancellationToken).ConfigureAwait(false);
            await member.WriteAtAsync(page, ErasureStreamSuperblockSerializer.PageSize, cancellationToken).ConfigureAwait(false);
        }

        await Task.WhenAll(members.Select(member => member!.Stream.FlushAsync(cancellationToken))).ConfigureAwait(false);
    }

    private ErasureStreamHeader CreateHeader(
        int position,
        Guid[] ids,
        int parityCount,
        Guid configurationId,
        ulong generation) =>
        new(
            ErasureStreamSuperblockSerializer.MajorVersion,
            ErasureStreamSuperblockSerializer.MinorVersion,
            0,
            0,
            SetId,
            configurationId,
            generation,
            ids[position],
            checked((ushort)position),
            checked((ushort)DataShardCount),
            checked((ushort)parityCount),
            ErasureStreamSuperblockSerializer.CodecId,
            ErasureStreamSuperblockSerializer.LayoutId,
            checked((uint)BlockSize),
            checked((uint)BlockSize),
            checked((ulong)DataOffset),
            checked((ulong)LogicalLength),
            checked((uint)BlockSize),
            Array.AsReadOnly((Guid[])ids.Clone()));

    private static void ValidateCompatibleHeader(ErasureStreamHeader basis, ErasureStreamHeader candidate)
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

        long remaining = dataOffset - ErasureStreamSuperblockSerializer.PageSize;
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

    private CacheEntry AcquireEntry(long codeword)
    {
        lock (_cacheSync)
        {
            if (!_cache.TryGetValue(codeword, out CacheEntry? entry))
            {
                entry = new CacheEntry(codeword, _members.Length);
                _cache.Add(codeword, entry);
                entry.LruNode = _cacheLru.AddLast(entry);
            }

            entry.RecentlyUsed = true;
            entry.References++;
            return entry;
        }
    }

    private void ReleaseEntry(CacheEntry entry)
    {
        lock (_cacheSync)
        {
            entry.References--;
            EvictUnderLock();
        }
    }

    private byte[] RentShard(CacheEntry entry, int index)
    {
        byte[]? existing = entry.Shards[index];
        if (existing is not null)
        {
            return existing;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(BlockSize);
        rented.AsSpan(0, BlockSize).Clear();
        entry.Shards[index] = rented;
        Interlocked.Add(ref _cacheBytes, BlockSize);
        return rented;
    }

    private void EvictUnderLock()
    {
        while (_cacheBytes > _options.MaximumCacheBytes)
        {
            CacheEntry? candidate = FindEvictionCandidateUnderLock();
            if (candidate is null)
            {
                return;
            }

            RemoveEntryUnderLock(candidate);
        }
    }

    private CacheEntry? FindEvictionCandidateUnderLock()
    {
        long attemptsRemaining = (long)_cacheLru.Count * 2;
        while (attemptsRemaining-- > 0 && _cacheLru.First is { } node)
        {
            CacheEntry candidate = node.Value;
            bool canEvict = candidate.References == 0 &&
                (!CanRead || CanReadAt || candidate.Codeword != _forwardReadCodeword) &&
                (!CanWrite || CanWriteAt || _forwardWriteCompleted || candidate.Codeword != _forwardWriteCodeword);
            if (canEvict && !candidate.RecentlyUsed)
            {
                return candidate;
            }

            if (canEvict)
            {
                candidate.RecentlyUsed = false;
            }

            _cacheLru.RemoveFirst();
            _cacheLru.AddLast(node);
        }

        return null;
    }

    private void RemoveEntryUnderLock(CacheEntry entry)
    {
        _cache.Remove(entry.Codeword);
        if (entry.LruNode is not null)
        {
            _cacheLru.Remove(entry.LruNode);
            entry.LruNode = null;
        }

        ReturnEntryBuffers(entry);
        entry.Gate.Dispose();
    }

    private void EvictCompletedSequentialEntry(long codeword)
    {
        lock (_cacheSync)
        {
            if (_cache.TryGetValue(codeword, out CacheEntry? entry) && entry.References == 0)
            {
                RemoveEntryUnderLock(entry);
            }
        }
    }

    private void ClearCache()
    {
        lock (_cacheSync)
        {
            foreach (CacheEntry entry in _cache.Values)
            {
                ReturnEntryBuffers(entry);
                entry.Gate.Dispose();
                entry.LruNode = null;
            }

            _cache.Clear();
            _cacheLru.Clear();
            _cacheBytes = 0;
        }
    }

    private void ReturnEntryBuffers(CacheEntry entry)
    {
        foreach (byte[]? shard in entry.Shards)
        {
            if (shard is not null)
            {
                ArrayPool<byte>.Shared.Return(shard);
                Interlocked.Add(ref _cacheBytes, -BlockSize);
            }
        }
    }

    private void EnsureDataShardLoaded(CacheEntry entry, int dataIndex)
    {
        if (entry.Present[dataIndex])
        {
            return;
        }

        MemberAccessor? direct = _members[dataIndex];
        if (direct?.CanReadAt == true)
        {
            byte[] target = RentShard(entry, dataIndex);
            direct.ReadAtFill(target.AsSpan(0, BlockSize), MemberBlockOffset(entry.Codeword));
            entry.Present[dataIndex] = true;
            return;
        }

        LoadForReconstruction(entry);
    }

    private async ValueTask EnsureDataShardLoadedAsync(
        CacheEntry entry,
        int dataIndex,
        CancellationToken cancellationToken)
    {
        if (entry.Present[dataIndex])
        {
            return;
        }

        MemberAccessor? direct = _members[dataIndex];
        if (direct?.CanReadAt == true)
        {
            byte[] target = RentShard(entry, dataIndex);
            await direct.ReadAtFillAsync(
                target.AsMemory(0, BlockSize),
                MemberBlockOffset(entry.Codeword),
                cancellationToken).ConfigureAwait(false);
            entry.Present[dataIndex] = true;
            return;
        }

        await LoadForReconstructionAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    private void LoadForReconstruction(CacheEntry entry)
    {
        int loaded = entry.Present.Count(static value => value);
        for (int index = 0; index < _members.Length && loaded < DataShardCount; index++)
        {
            if (entry.Present[index] || _members[index]?.CanReadAt != true)
            {
                continue;
            }

            byte[] shard = RentShard(entry, index);
            _members[index]!.ReadAtFill(shard.AsSpan(0, BlockSize), MemberBlockOffset(entry.Codeword));
            entry.Present[index] = true;
            loaded++;
        }

        if (loaded < DataShardCount)
        {
            throw new IOException("Fewer than the required number of member blocks are readable.");
        }

        EnsureEveryShardAllocated(entry);
        _codec.Reconstruct(entry.Shards!, entry.Present, 0, BlockSize);
    }

    private async ValueTask LoadForReconstructionAsync(CacheEntry entry, CancellationToken cancellationToken)
    {
        int loaded = entry.Present.Count(static value => value);
        for (int index = 0; index < _members.Length && loaded < DataShardCount; index++)
        {
            if (entry.Present[index] || _members[index]?.CanReadAt != true)
            {
                continue;
            }

            byte[] shard = RentShard(entry, index);
            await _members[index]!.ReadAtFillAsync(
                shard.AsMemory(0, BlockSize),
                MemberBlockOffset(entry.Codeword),
                cancellationToken).ConfigureAwait(false);
            entry.Present[index] = true;
            loaded++;
        }

        if (loaded < DataShardCount)
        {
            throw new IOException("Fewer than the required number of member blocks are readable.");
        }

        EnsureEveryShardAllocated(entry);
        _codec.Reconstruct(entry.Shards!, entry.Present, 0, BlockSize);
    }

    private void EnsureAllDataLoaded(CacheEntry entry)
    {
        for (int index = 0; index < DataShardCount; index++)
        {
            EnsureDataShardLoaded(entry, index);
        }
    }

    private async ValueTask EnsureAllDataLoadedAsync(CacheEntry entry, CancellationToken cancellationToken)
    {
        for (int index = 0; index < DataShardCount; index++)
        {
            await EnsureDataShardLoadedAsync(entry, index, cancellationToken).ConfigureAwait(false);
        }
    }

    private void EnsureEveryShardAllocated(CacheEntry entry)
    {
        for (int index = 0; index < entry.Shards.Length; index++)
        {
            RentShard(entry, index);
        }
    }

    private void EncodeParity(CacheEntry entry)
    {
        EnsureEveryShardAllocated(entry);
        _codec.Encode(entry.Shards!, 0, BlockSize);
        Array.Fill(entry.Present, true);
    }

    private DataShardRange ApplyToData(CacheEntry entry, ReadOnlySpan<byte> source, int withinCodeword)
    {
        int firstTouched = withinCodeword / BlockSize;
        int lastTouched = firstTouched;
        int completed = 0;
        while (completed < source.Length)
        {
            int position = withinCodeword + completed;
            int dataIndex = position / BlockSize;
            int within = position % BlockSize;
            int count = Math.Min(source.Length - completed, BlockSize - within);
            source.Slice(completed, count).CopyTo(entry.Shards[dataIndex]!.AsSpan(within, count));
            lastTouched = dataIndex;
            completed += count;
        }

        return new DataShardRange(firstTouched, lastTouched);
    }

    private void WriteChangedMembers(CacheEntry entry, DataShardRange touched)
    {
        long offset = MemberBlockOffset(entry.Codeword);
        for (int index = 0; index < _members.Length; index++)
        {
            if (index < DataShardCount && !touched.Contains(index))
            {
                continue;
            }

            _members[index]!.WriteAt(entry.Shards[index]!.AsSpan(0, BlockSize), offset);
        }
    }

    private async ValueTask WriteChangedMembersAsync(
        CacheEntry entry,
        DataShardRange touched,
        CancellationToken cancellationToken)
    {
        long offset = MemberBlockOffset(entry.Codeword);
        int pendingCount = 0;
        for (int index = 0; index < _members.Length; index++)
        {
            if (index < DataShardCount && !touched.Contains(index))
            {
                continue;
            }

            ValueTask write = _members[index]!
                .WriteAtAsync(entry.Shards[index]!.AsMemory(0, BlockSize), offset, cancellationToken);
            if (write.IsCompletedSuccessfully)
            {
                write.GetAwaiter().GetResult();
            }
            else
            {
                entry.PendingWriteTasks[pendingCount++] = write.AsTask();
            }
        }

        Exception? firstError = null;
        for (int index = 0; index < pendingCount; index++)
        {
            try
            {
                await entry.PendingWriteTasks[index]!.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                firstError ??= error;
            }
            finally
            {
                entry.PendingWriteTasks[index] = null;
            }
        }

        if (firstError is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstError).Throw();
        }
    }

    private int ReadForward(Span<byte> buffer, long offset)
    {
        byte[] temporary = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            int read = ReadForwardAsyncCore(
                temporary.AsMemory(0, buffer.Length),
                offset,
                CancellationToken.None).AsTask().GetAwaiter().GetResult();
            temporary.AsSpan(0, read).CopyTo(buffer);
            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(temporary);
        }
    }

    private async ValueTask<int> ReadForwardAsync(
        Memory<byte> buffer,
        long offset,
        CancellationToken cancellationToken) =>
        await ReadForwardAsyncCore(buffer, offset, cancellationToken).ConfigureAwait(false);

    private async ValueTask<int> ReadForwardAsyncCore(
        Memory<byte> temporary,
        long offset,
        CancellationToken cancellationToken)
    {
        using OperationLease operation = EnterOperation();
        if (!CanRead)
        {
            throw new NotSupportedException("Fewer than the required number of members are readable.");
        }

        int requested = ValidateReadRange(offset, temporary.Length);
        int completed = 0;
        while (completed < requested)
        {
            MapLogicalOffset(offset + completed, out long codeword, out int dataIndex, out int within);
            if (codeword != _forwardReadCodeword)
            {
                throw new NotSupportedException("Forward-only members cannot revisit or skip codewords.");
            }

            CacheEntry entry = AcquireEntry(codeword);
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureSequentialCodewordLoadedAsync(entry, cancellationToken).ConfigureAwait(false);
                int count = Math.Min(requested - completed, BlockSize - within);
                entry.Shards[dataIndex]!.AsMemory(within, count).CopyTo(temporary[completed..]);
                completed += count;
                if ((offset + completed) / ((long)DataShardCount * BlockSize) > codeword)
                {
                    _forwardReadCodeword++;
                }
            }
            finally
            {
                entry.Gate.Release();
                ReleaseEntry(entry);
            }
        }

        return completed;
    }

    private void WriteForward(ReadOnlySpan<byte> source, long offset) =>
        WriteForwardAsync(source.ToArray(), offset, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    private async ValueTask WriteForwardAsync(
        ReadOnlyMemory<byte> source,
        long offset,
        CancellationToken cancellationToken)
    {
        using OperationLease operation = EnterOperation();
        if (!CanWrite)
        {
            throw new NotSupportedException("Every configured member must be writable.");
        }

        ValidateWriteRange(offset, source.Length);
        if (_forwardWriteCompleted)
        {
            throw new InvalidOperationException("The forward-only stream has been completed.");
        }

        int completed = 0;
        long codewordBytes = checked((long)DataShardCount * BlockSize);
        while (completed < source.Length)
        {
            long logical = offset + completed;
            long codeword = logical / codewordBytes;
            if (codeword != _forwardWriteCodeword)
            {
                throw new NotSupportedException("Forward-only members cannot revisit or skip codewords.");
            }

            int withinCodeword = checked((int)(logical % codewordBytes));
            int count = Math.Min(source.Length - completed, checked((int)(codewordBytes - withinCodeword)));
            CacheEntry entry = AcquireEntry(codeword);
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureEveryShardAllocated(entry);
                ApplyToData(entry, source.Span.Slice(completed, count), withinCodeword);
                if (withinCodeword + count == codewordBytes)
                {
                    EncodeParity(entry);
                    await WriteSequentialCodewordAsync(entry, cancellationToken).ConfigureAwait(false);
                    _forwardWriteCodeword++;
                }
            }
            finally
            {
                entry.Gate.Release();
                ReleaseEntry(entry);
            }

            completed += count;
        }
    }

    private async ValueTask EnsureSequentialCodewordLoadedAsync(
        CacheEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.Present.Count(static value => value) >= DataShardCount)
        {
            if (!entry.Present.Take(DataShardCount).All(static value => value))
            {
                EnsureEveryShardAllocated(entry);
                _codec.Reconstruct(entry.Shards!, entry.Present, 0, BlockSize);
            }

            return;
        }

        for (int index = 0; index < _members.Length; index++)
        {
            MemberAccessor? member = _members[index];
            if (member?.CanRead != true)
            {
                continue;
            }

            byte[] shard = RentShard(entry, index);
            await member.Stream.ReadExactlyAsync(shard.AsMemory(0, BlockSize), cancellationToken).ConfigureAwait(false);
            entry.Present[index] = true;
        }

        if (entry.Present.Count(static value => value) < DataShardCount)
        {
            throw new IOException("Fewer than the required number of sequential member blocks are readable.");
        }

        EnsureEveryShardAllocated(entry);
        _codec.Reconstruct(entry.Shards!, entry.Present, 0, BlockSize);
    }

    private async ValueTask WriteSequentialCodewordAsync(CacheEntry entry, CancellationToken cancellationToken)
    {
        Task[] writes = _members.Select((member, index) =>
            member!.Stream.WriteAsync(entry.Shards[index]!.AsMemory(0, BlockSize), cancellationToken).AsTask()).ToArray();
        await Task.WhenAll(writes).ConfigureAwait(false);
    }

    private void ScheduleReadAhead(long nextOffset)
    {
        if (_options.ReadAheadBlockCount == 0 || nextOffset >= LogicalLength || !CanReadAt || IsDisposed)
        {
            return;
        }

        long logicalBlock = nextOffset / BlockSize;
        if (nextOffset % BlockSize != 0)
        {
            logicalBlock++;
        }
        for (int ahead = 0; ahead < _options.ReadAheadBlockCount; ahead++)
        {
            long candidate = logicalBlock + ahead;
            if (candidate * BlockSize >= LogicalLength)
            {
                break;
            }

            long codeword = candidate / DataShardCount;
            int dataIndex = checked((int)(candidate % DataShardCount));
            Task task = PrefetchAsync(codeword, dataIndex, _prefetchCancellation.Token);
            lock (_cacheSync)
            {
                _prefetchTasks.Add(task);
            }

            _ = task.ContinueWith(
                completed =>
                {
                    lock (_cacheSync)
                    {
                        _prefetchTasks.Remove(completed);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task PrefetchAsync(long codeword, int dataIndex, CancellationToken cancellationToken)
    {
        try
        {
            CacheEntry entry = AcquireEntry(codeword);
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureDataShardLoadedAsync(entry, dataIndex, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                entry.Gate.Release();
                ReleaseEntry(entry);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Speculative reads never fault foreground I/O.
        }
    }

    private async ValueTask FlushMembersAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await Task.WhenAll(_members
            .Where(static member => member is not null)
            .Select(member => member!.Stream.FlushAsync(cancellationToken))).ConfigureAwait(false);
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

    private async ValueTask BeginMaintenanceAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _maintenanceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Task? drained = null;
            lock (_operationSync)
            {
                _maintenanceActive = true;
                if (_activeOperations != 0)
                {
                    _operationsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    drained = _operationsDrained.Task;
                }
            }

            if (drained is not null)
            {
                await drained.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            Task[] prefetch;
            lock (_cacheSync)
            {
                prefetch = _prefetchTasks.ToArray();
            }

            await Task.WhenAll(prefetch).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            EndMaintenance();
            throw;
        }
    }

    private void EndMaintenance()
    {
        lock (_operationSync)
        {
            _maintenanceActive = false;
        }

        _maintenanceGate.Release();
    }

    private OperationLease EnterOperation()
    {
        ThrowIfDisposed();
        lock (_operationSync)
        {
            if (_maintenanceActive)
            {
                throw new InvalidOperationException("A parity-maintenance operation is active.");
            }

            _activeOperations++;
            return new OperationLease(this);
        }
    }

    private void ExitOperation()
    {
        TaskCompletionSource? drained = null;
        lock (_operationSync)
        {
            _activeOperations--;
            if (_activeOperations == 0 && _maintenanceActive)
            {
                drained = _operationsDrained;
                _operationsDrained = null;
            }
        }

        drained?.TrySetResult();
    }

    private int ValidateReadRange(long offset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return offset >= LogicalLength ? 0 : checked((int)Math.Min(count, LogicalLength - offset));
    }

    private void ValidateWriteRange(long offset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > LogicalLength || count > LogicalLength - offset)
        {
            throw new ArgumentException("The write exceeds the fixed logical length.", nameof(count));
        }
    }

    private static void ValidateGeometry(int dataCount, int parityCount, long length, int blockSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dataCount, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(parityCount, 1);
        if (dataCount + parityCount > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(parityCount));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (blockSize is < 4096 or > 1024 * 1024 || !int.IsPow2(blockSize))
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize), "Block size must be a power of two from 4 KiB through 1 MiB.");
        }
    }

    private void MapLogicalOffset(long logical, out long codeword, out int dataIndex, out int within)
    {
        long logicalBlock = logical / BlockSize;
        codeword = logicalBlock / DataShardCount;
        dataIndex = checked((int)(logicalBlock % DataShardCount));
        within = checked((int)(logical % BlockSize));
    }

    private long MemberBlockOffset(long codeword) => checked(DataOffset + codeword * BlockSize);

    private void EnsureCanReadAt()
    {
        if (!CanReadAt)
        {
            throw new NotSupportedException("At least k members must support positional reads.");
        }
    }

    private void EnsureCanWriteAt()
    {
        if (!CanWriteAt)
        {
            throw new NotSupportedException("Every member must support positional writes and every data member must support positional reads.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    private void WaitForPrefetch()
    {
        Task[] tasks;
        lock (_cacheSync)
        {
            tasks = _prefetchTasks.ToArray();
        }

        Task.WhenAll(tasks).GetAwaiter().GetResult();
    }

    private void DisposeMembers(bool disposeStreams = true)
    {
        foreach (MemberAccessor? member in _members)
        {
            if (member is null)
            {
                continue;
            }

            if (disposeStreams)
            {
                member.Stream.Dispose();
            }

            member.Dispose();
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

    private sealed class CacheEntry(long codeword, int memberCount)
    {
        internal long Codeword { get; } = codeword;
        internal byte[]?[] Shards { get; } = new byte[memberCount][];
        internal bool[] Present { get; } = new bool[memberCount];
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal LinkedListNode<CacheEntry>? LruNode { get; set; }
        internal Task?[] PendingWriteTasks { get; } = new Task[memberCount];
        internal bool RecentlyUsed { get; set; }
        internal int References { get; set; }
    }

    private readonly record struct DataShardRange(int First, int Last)
    {
        internal bool Contains(int index) => index >= First && index <= Last;
    }

    private sealed class MemberAccessor : IDisposable
    {
        private readonly ITeeRandomAccessStream? _randomAccess;
        private readonly SemaphoreSlim _gate = new(1, 1);

        internal MemberAccessor(Stream stream)
        {
            Stream = stream;
            TeeRandomAccess.TryGet(stream, out _randomAccess);
        }

        internal Stream Stream { get; }
        internal bool CanRead => Stream.CanRead;
        internal bool CanWrite => Stream.CanWrite;
        internal bool CanReadAt => _randomAccess?.CanReadAt == true || (Stream.CanRead && Stream.CanSeek);
        internal bool CanWriteAt => _randomAccess?.CanWriteAt == true || (Stream.CanWrite && Stream.CanSeek);

        internal void ReadAtFill(Span<byte> destination, long offset)
        {
            destination.Clear();
            int completed = 0;
            if (_randomAccess?.CanReadAt == true)
            {
                while (completed < destination.Length)
                {
                    int read = _randomAccess.ReadAt(destination[completed..], offset + completed);
                    if (read == 0)
                    {
                        break;
                    }

                    completed += read;
                }

                return;
            }

            _gate.Wait();
            try
            {
                Stream.Position = offset;
                while (completed < destination.Length)
                {
                    int read = Stream.Read(destination[completed..]);
                    if (read == 0)
                    {
                        break;
                    }

                    completed += read;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        internal async ValueTask ReadAtFillAsync(
            Memory<byte> destination,
            long offset,
            CancellationToken cancellationToken)
        {
            destination.Span.Clear();
            int completed = 0;
            if (_randomAccess?.CanReadAt == true)
            {
                while (completed < destination.Length)
                {
                    int read = await _randomAccess
                        .ReadAtAsync(destination[completed..], offset + completed, cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    completed += read;
                }

                return;
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Stream.Position = offset;
                while (completed < destination.Length)
                {
                    int read = await Stream.ReadAsync(destination[completed..], cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    completed += read;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        internal void WriteAt(ReadOnlySpan<byte> source, long offset)
        {
            if (_randomAccess?.CanWriteAt == true)
            {
                _randomAccess.WriteAt(source, offset);
                return;
            }

            _gate.Wait();
            try
            {
                Stream.Position = offset;
                Stream.Write(source);
            }
            finally
            {
                _gate.Release();
            }
        }

        internal ValueTask WriteAtAsync(
            ReadOnlyMemory<byte> source,
            long offset,
            CancellationToken cancellationToken)
        {
            if (_randomAccess?.CanWriteAt == true)
            {
                return _randomAccess.WriteAtAsync(source, offset, cancellationToken);
            }

            return WriteAtFallbackAsync(source, offset, cancellationToken);
        }

        private async ValueTask WriteAtFallbackAsync(
            ReadOnlyMemory<byte> source,
            long offset,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Stream.Position = offset;
                await Stream.WriteAsync(source, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose() => _gate.Dispose();
    }

    private readonly struct OperationLease(ErasureStream owner) : IDisposable
    {
        public void Dispose() => owner.ExitOperation();
    }
}
