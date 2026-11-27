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
    private readonly object _disposeSync = new();
    private readonly Dictionary<long, CacheEntry> _cache = [];
    private readonly LinkedList<CacheEntry> _cacheLru = [];
    private readonly SemaphoreSlim _positionGate = new(1, 1);
    private readonly SemaphoreSlim _maintenanceGate = new(1, 1);
    private readonly CancellationTokenSource _prefetchCancellation = new();
    private readonly HashSet<Task> _prefetchTasks = [];
    private readonly ErasureStreamOptions _options;
    internal MemberAccessor?[] _members;
    internal ReedSolomonCodec _codec;
    internal int _parityShardCount;
    private long _position;
    private long _cacheBytes;
    private long _forwardReadCodeword;
    private long _forwardWriteCodeword;
    private bool _forwardWriteCompleted;
    private bool _maintenanceActive;
    private int _activeOperations;
    private TaskCompletionSource? _operationsDrained;
    private int _disposed;
    private Task? _disposeTask;

    internal ErasureStream(
        MemberAccessor?[] members,
        int dataShardCount,
        int parityShardCount,
        int blockSize,
        long logicalLength,
        long dataOffset,
        ErasureStreamOptions options)
    {
        _members = members;
        DataShardCount = dataShardCount;
        _parityShardCount = parityShardCount;
        BlockSize = blockSize;
        LogicalLength = logicalLength;
        DataOffset = dataOffset;
        _options = options;
        _codec = new ReedSolomonCodec(dataShardCount, parityShardCount);
    }

    /// <summary>Creates a headerless stream over the supplied members.</summary>
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

        var result = new ErasureStream(
            accessors, dataShardCount, parityShardCount, blockSize, logicalLength, 0, resolved);

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

    /// <summary>Opens a raw set whose member positions and geometry are supplied externally.</summary>
    public static ErasureStream Open(
        IReadOnlyList<Stream?> members,
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
            throw new ArgumentException($"Exactly {count} member positions are required.", nameof(members));
        }

        var accessors = new MemberAccessor?[count];
        var seen = new HashSet<Stream>(ReferenceEqualityComparer.Instance);
        long codewordBytes = (long)dataShardCount * blockSize;
        long codewordCount = logicalLength == 0 ? 0 : 1 + (logicalLength - 1) / codewordBytes;
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

            if (member.CanSeek)
            {
                long length = member.Length;
                if (length % blockSize != 0 && length / blockSize < codewordCount)
                {
                    throw new EndOfStreamException($"Erasure member {index} ends partway through a stored block.");
                }
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
            dataShardCount,
            parityShardCount,
            blockSize,
            logicalLength,
            0,
            resolved);
    }

    /// <summary>Gets the number of systematic data members.</summary>
    public int DataShardCount { get; }

    /// <summary>Gets the current number of configured parity members.</summary>
    public int ParityShardCount => Volatile.Read(ref _parityShardCount);

    /// <summary>Gets the payload block size stored by each member.</summary>
    public int BlockSize { get; }

    /// <summary>Gets the physical offset at which member data begins.</summary>
    internal long DataOffset { get; }

    /// <summary>Gets the immutable logical stream length.</summary>
    public long LogicalLength { get; }

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
        using OperationLease operation = EnterOperation();
        _positionGate.Wait();
        try
        {
            ThrowIfDisposed();
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
        using OperationLease operation = EnterOperation();
        await _positionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
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
    public int ReadAt(Span<byte> buffer, long offset)
    {
        using OperationLease operation = EnterOperation();
        return ReadAtCore(buffer, offset, streaming: false);
    }

    private int ReadAtCore(Span<byte> buffer, long offset, bool streaming)
    {
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
    public async ValueTask<int> ReadAtAsync(
        Memory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default)
    {
        using OperationLease operation = EnterOperation();
        return await ReadAtAsyncCore(buffer, offset, streaming: false, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<int> ReadAtAsyncCore(
        Memory<byte> buffer,
        long offset,
        bool streaming,
        CancellationToken cancellationToken)
    {
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
            CacheEntry entry = await AcquireLockedEntryAsync(codeword, cancellationToken).ConfigureAwait(false);
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
        using OperationLease operation = EnterOperation();
        _positionGate.Wait();
        try
        {
            ThrowIfDisposed();
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
        using OperationLease operation = EnterOperation();
        await _positionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
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
    public void WriteAt(ReadOnlySpan<byte> buffer, long offset)
    {
        using OperationLease operation = EnterOperation();
        WriteAtCore(buffer, offset, streaming: false);
    }

    private void WriteAtCore(ReadOnlySpan<byte> buffer, long offset, bool streaming)
    {
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
    public async ValueTask WriteAtAsync(
        ReadOnlyMemory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default)
    {
        using OperationLease operation = EnterOperation();
        await WriteAtAsyncCore(buffer, offset, streaming: false, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteAtAsyncCore(
        ReadOnlyMemory<byte> buffer,
        long offset,
        bool streaming,
        CancellationToken cancellationToken)
    {
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
            CacheEntry entry = await AcquireLockedEntryAsync(codeword, cancellationToken).ConfigureAwait(false);
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
                await WriteMembersAsync(entry, touched, cancellationToken).ConfigureAwait(false);
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
        using OperationLease operation = EnterOperation();
        await _positionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!CanWriteAt && !_forwardWriteCompleted)
            {
                if (_position != LogicalLength)
                {
                    throw new InvalidOperationException("A forward-only stream must receive its complete declared logical length.");
                }

                if (_forwardWriteCodeword * (long)DataShardCount * BlockSize < LogicalLength)
                {
                    CacheEntry entry = await AcquireLockedEntryAsync(_forwardWriteCodeword, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        EnsureEveryShardAllocated(entry);
                        EncodeParity(entry);
                        await WriteMembersAsync(entry, positionalChanges: null, cancellationToken).ConfigureAwait(false);
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
        using OperationLease operation = EnterOperation();
        foreach (MemberAccessor? member in _members)
        {
            member?.Stream.Flush();
        }
    }

    /// <inheritdoc/>
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        using OperationLease operation = EnterOperation();
        await FlushMembersAsync(cancellationToken).ConfigureAwait(false);
    }

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

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            BeginDispose(asynchronously: false).GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        await BeginDispose(asynchronously: true).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private Task BeginDispose(bool asynchronously)
    {
        lock (_disposeSync)
        {
            return _disposeTask ??= ReleaseResourcesAsync(asynchronously);
        }
    }

    private async Task ReleaseResourcesAsync(bool asynchronously)
    {
        Volatile.Write(ref _disposed, 1);
        _prefetchCancellation.Cancel();
        await BeginMaintenanceAsync(CancellationToken.None, disposing: true).ConfigureAwait(false);
        EndMaintenance();
        ClearCache();
        try
        {
            if (asynchronously && !_options.LeaveOpen)
            {
                foreach (MemberAccessor? member in _members)
                {
                    if (member is not null)
                    {
                        await member.Stream.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            _positionGate.Dispose();
            _prefetchCancellation.Dispose();
            DisposeMembers(disposeStreams: !asynchronously && !_options.LeaveOpen);
        }
    }

    private int ReadableMemberCount => _members.Count(static member => member?.CanRead == true);

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal long GetCodewordCount()
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
    }

    internal CacheEntry AcquireEntry(long codeword)
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

    internal async ValueTask<CacheEntry> AcquireLockedEntryAsync(long codeword, CancellationToken cancellationToken)
    {
        CacheEntry entry = AcquireEntry(codeword);
        try
        {
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return entry;
        }
        catch
        {
            ReleaseEntry(entry);
            throw;
        }
    }

    internal void ReleaseEntry(CacheEntry entry)
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

    internal void ClearCache()
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

    internal async ValueTask EnsureAllDataLoadedAsync(CacheEntry entry, CancellationToken cancellationToken)
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

    internal void EncodeParity(CacheEntry entry)
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

    private async ValueTask WriteMembersAsync(
        CacheEntry entry,
        DataShardRange? positionalChanges,
        CancellationToken cancellationToken)
    {
        long offset = positionalChanges.HasValue ? MemberBlockOffset(entry.Codeword) : 0;
        int pendingCount = 0;
        Exception? firstError = null;
        try
        {
            for (int index = 0; index < _members.Length; index++)
            {
                if (positionalChanges is { } touched && index < DataShardCount && !touched.Contains(index))
                {
                    continue;
                }

                MemberAccessor member = _members[index]!;
                ReadOnlyMemory<byte> shard = entry.Shards[index]!.AsMemory(0, BlockSize);
                ValueTask write = positionalChanges.HasValue
                    ? member.WriteAtAsync(shard, offset, cancellationToken)
                    : member.Stream.WriteAsync(shard, cancellationToken);
                if (write.IsCompletedSuccessfully)
                {
                    write.GetAwaiter().GetResult();
                }
                else
                {
                    entry.PendingWriteTasks[pendingCount++] = write.AsTask();
                }
            }
        }
        catch (Exception error)
        {
            // Earlier members still own their shard buffers until their writes finish.
            firstError = error;
        }

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

            CacheEntry entry = await AcquireLockedEntryAsync(codeword, cancellationToken).ConfigureAwait(false);
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
            CacheEntry entry = await AcquireLockedEntryAsync(codeword, cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureEveryShardAllocated(entry);
                ApplyToData(entry, source.Span.Slice(completed, count), withinCodeword);
                if (withinCodeword + count == codewordBytes)
                {
                    EncodeParity(entry);
                    await WriteMembersAsync(entry, positionalChanges: null, cancellationToken).ConfigureAwait(false);
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
            CacheEntry entry = await AcquireLockedEntryAsync(codeword, cancellationToken).ConfigureAwait(false);
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
            .Select(async member => await member!.Stream.FlushAsync(cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
    }

    internal async ValueTask BeginMaintenanceAsync(CancellationToken cancellationToken, bool disposing = false)
    {
        await _maintenanceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!disposing)
            {
                ThrowIfDisposed();
            }

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

    internal void EndMaintenance()
    {
        lock (_operationSync)
        {
            _maintenanceActive = false;
        }

        _maintenanceGate.Release();
    }

    private OperationLease EnterOperation()
    {
        lock (_operationSync)
        {
            ThrowIfDisposed();
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

    internal static void ValidateGeometry(int dataCount, int parityCount, long length, int blockSize)
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

    internal void DisposeMembers(bool disposeStreams = true)
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

    internal sealed class CacheEntry(long codeword, int memberCount)
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

    internal sealed class MemberAccessor : IDisposable
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
                        ValidateBlockEnd(completed);
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
                        ValidateBlockEnd(completed);
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
                        ValidateBlockEnd(completed);
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
                        ValidateBlockEnd(completed);
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

        private static void ValidateBlockEnd(int bytesRead)
        {
            // Entire unwritten blocks in sparse sets read as zeros.
            if (bytesRead != 0)
            {
                throw new EndOfStreamException("An erasure member ended partway through a stored block.");
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
