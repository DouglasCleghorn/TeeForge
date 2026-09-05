using System.Buffers;
using System.Text;
using TeeForge.RandomAccess;
using TeeForge.RandomAccess.Internal;
using TeeForge.Experimental.Storage.Sparse.Internal;

namespace TeeForge.Experimental.Storage.Sparse;

#pragma warning disable RS0026 // Identity-supplying overloads are intentionally distinct from capability-discovering overloads.

/// <summary>Overlays VHDX-style child changes on a read-only base stream.</summary>
/// <remarks>Experimental research API, not ready for production use. API and format compatibility are not guaranteed.</remarks>
public class DifferencingDiskImage : Stream, ITeeRandomAccessStream, ITeeRangeReadSource,
    IVirtualDiskStream, IDependentStreamRegistry
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private readonly Stream _baseStream;
    private readonly Stream _differenceStream;
    private readonly ITeeRandomAccessStream? _baseRandomAccess;
    private readonly ITeeRandomAccessStream? _differenceRandomAccess;
    private readonly DifferencingDiskImageOptions _options;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Dictionary<long, BlockState> _blocks = [];
    private readonly HashSet<Guid> _dependentStreamIds = [];

    private DifferencingRoot _root;
    private long _position;
    private long _logicalLength;
    private long _nextAppendOffset;
    private bool _readOnly;
    private bool _dataWriteIdAdvanced;
    private bool _disposed;

    private DifferencingDiskImage(
        Stream baseStream,
        Stream differenceStream,
        DifferencingDiskImageOptions options)
    {
        _baseStream = baseStream;
        _differenceStream = differenceStream;
        _options = options;
        TeeRandomAccess.TryGet(baseStream, out _baseRandomAccess);
        TeeRandomAccess.TryGet(differenceStream, out _differenceRandomAccess);
    }

    /// <summary>Reads and validates the parent locator without opening the base stream.</summary>
    public static DifferencingDiskImageLocator ReadLocator(Stream differenceStream)
    {
        ArgumentNullException.ThrowIfNull(differenceStream);
        if (!differenceStream.CanRead || !differenceStream.CanSeek)
        {
            throw new ArgumentException("Locator inspection requires a readable, seekable difference stream.", nameof(differenceStream));
        }

        byte[] identifierBuffer = new byte[DifferencingFormat.SectorSize];
        if (TeeRandomAccess.TryGet(differenceStream, out ITeeRandomAccessStream? randomAccess))
        {
            int totalRead = 0;
            while (totalRead < identifierBuffer.Length)
            {
                int read = randomAccess.ReadAt(
                    identifierBuffer.AsSpan(totalRead),
                    DifferencingFormat.IdentifierOffset + totalRead);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            if (totalRead != identifierBuffer.Length)
            {
                throw Corruption("The difference stream is too short to contain its identifier.");
            }
        }
        else
        {
            long position = differenceStream.Position;
            try
            {
                differenceStream.Position = DifferencingFormat.IdentifierOffset;
                differenceStream.ReadExactly(identifierBuffer);
            }
            catch (EndOfStreamException exception)
            {
                throw Corruption("The difference stream is too short to contain its identifier.", innerException: exception);
            }
            finally
            {
                differenceStream.Position = position;
            }
        }

        return ParseLocator(identifierBuffer);
    }

    /// <summary>Asynchronously reads and validates the parent locator without opening the base stream.</summary>
    public static async ValueTask<DifferencingDiskImageLocator> ReadLocatorAsync(
        Stream differenceStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(differenceStream);
        if (!differenceStream.CanRead || !differenceStream.CanSeek)
        {
            throw new ArgumentException("Locator inspection requires a readable, seekable difference stream.", nameof(differenceStream));
        }

        byte[] identifierBuffer = new byte[DifferencingFormat.SectorSize];
        if (TeeRandomAccess.TryGet(differenceStream, out ITeeRandomAccessStream? randomAccess))
        {
            int totalRead = 0;
            while (totalRead < identifierBuffer.Length)
            {
                int read = await randomAccess.ReadAtAsync(
                    identifierBuffer.AsMemory(totalRead),
                    DifferencingFormat.IdentifierOffset + totalRead,
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            if (totalRead != identifierBuffer.Length)
            {
                throw Corruption("The difference stream is too short to contain its identifier.");
            }
        }
        else
        {
            long position = differenceStream.Position;
            try
            {
                differenceStream.Position = DifferencingFormat.IdentifierOffset;
                await differenceStream.ReadExactlyAsync(identifierBuffer, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException exception)
            {
                throw Corruption("The difference stream is too short to contain its identifier.", innerException: exception);
            }
            finally
            {
                differenceStream.Position = position;
            }
        }

        return ParseLocator(identifierBuffer);
    }

    /// <summary>Creates a child using geometry and identity exposed by a TeeForge base.</summary>
    public static DifferencingDiskImage Create(
        Stream baseStream,
        Stream differenceStream,
        DifferencingDiskImageOptions? options = null,
        string? parentPathHint = null)
    {
        ArgumentNullException.ThrowIfNull(baseStream);
        if (baseStream is not IVirtualDiskStream virtualDisk)
        {
            throw new ArgumentException(
                "A base that does not expose TeeForge virtual-disk geometry requires the explicit identity overload.",
                nameof(baseStream));
        }

        return Create(
            baseStream,
            differenceStream,
            new StreamIdentity(virtualDisk.Id, virtualDisk.DataWriteId),
            virtualDisk.VirtualCapacity,
            virtualDisk.BlockSize,
            options,
            parentPathHint);
    }

    /// <summary>Creates a child using caller-supplied identity and geometry for an arbitrary base.</summary>
    public static DifferencingDiskImage Create(
        Stream baseStream,
        Stream differenceStream,
        StreamIdentity baseIdentity,
        long virtualCapacity,
        int blockSize = DynamicAllocationFormat.DefaultBlockSize,
        DifferencingDiskImageOptions? options = null,
        string? parentPathHint = null)
    {
        ArgumentNullException.ThrowIfNull(baseStream);
        ArgumentNullException.ThrowIfNull(differenceStream);
        options ??= DifferencingDiskImageOptions.Default;
        var stream = new DifferencingDiskImage(baseStream, differenceStream, options);
        stream.InitializeCreate(baseIdentity, virtualCapacity, blockSize, parentPathHint);
        return stream;
    }

    /// <summary>Asynchronously creates a child using geometry and identity exposed by a TeeForge base.</summary>
    public static ValueTask<DifferencingDiskImage> CreateAsync(
        Stream baseStream,
        Stream differenceStream,
        DifferencingDiskImageOptions? options = null,
        string? parentPathHint = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Create(baseStream, differenceStream, options, parentPathHint));
    }

    /// <summary>Asynchronously creates a child with caller-supplied base identity and geometry.</summary>
    public static ValueTask<DifferencingDiskImage> CreateAsync(
        Stream baseStream,
        Stream differenceStream,
        StreamIdentity baseIdentity,
        long virtualCapacity,
        int blockSize = DynamicAllocationFormat.DefaultBlockSize,
        DifferencingDiskImageOptions? options = null,
        string? parentPathHint = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Create(
            baseStream,
            differenceStream,
            baseIdentity,
            virtualCapacity,
            blockSize,
            options,
            parentPathHint));
    }

    /// <summary>Opens a child and obtains the current base identity from the base stream.</summary>
    public static DifferencingDiskImage Open(
        Stream baseStream,
        Stream differenceStream,
        DifferencingDiskImageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(baseStream);
        if (baseStream is not IStreamIdentity identity)
        {
            throw new ArgumentException(
                "A base that does not expose identity requires the explicit identity overload.",
                nameof(baseStream));
        }

        return Open(
            baseStream,
            differenceStream,
            new StreamIdentity(identity.Id, identity.DataWriteId),
            options);
    }

    /// <summary>Opens a child with caller-supplied current base identity.</summary>
    public static DifferencingDiskImage Open(
        Stream baseStream,
        Stream differenceStream,
        StreamIdentity baseIdentity,
        DifferencingDiskImageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(baseStream);
        ArgumentNullException.ThrowIfNull(differenceStream);
        options ??= DifferencingDiskImageOptions.Default;
        var stream = new DifferencingDiskImage(baseStream, differenceStream, options);
        stream.InitializeOpen(baseIdentity);
        return stream;
    }

    /// <summary>Asynchronously opens a child and obtains base identity from the base stream.</summary>
    public static ValueTask<DifferencingDiskImage> OpenAsync(
        Stream baseStream,
        Stream differenceStream,
        DifferencingDiskImageOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Open(baseStream, differenceStream, options));
    }

    /// <summary>Asynchronously opens a child with caller-supplied current base identity.</summary>
    public static ValueTask<DifferencingDiskImage> OpenAsync(
        Stream baseStream,
        Stream differenceStream,
        StreamIdentity baseIdentity,
        DifferencingDiskImageOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Open(baseStream, differenceStream, baseIdentity, options));
    }

    /// <inheritdoc />
    public Guid Id => _root.Id;

    /// <inheritdoc />
    public Guid DataWriteId => _root.DataWriteId;

    /// <summary>Gets the recorded immediate-base identifier.</summary>
    public Guid BaseId => _root.BaseId;

    /// <summary>Gets the recorded immediate-base data generation.</summary>
    public Guid BaseDataWriteId => _root.BaseDataWriteId;

    /// <inheritdoc />
    public int BlockSize => _root.BlockSize;

    /// <inheritdoc />
    public long VirtualCapacity => _root.VirtualCapacity;

    /// <summary>Gets whether this child is read-only.</summary>
    public bool IsReadOnly => _readOnly;

    /// <summary>Gets the base stream. Ordinary child I/O never writes to it.</summary>
    public Stream BaseStream => _baseStream;

    /// <summary>Gets the physical stream containing child state.</summary>
    public Stream DifferenceStream => _differenceStream;

    /// <summary>Gets the optional relative parent-locator hint.</summary>
    public string? ParentPathHint { get; private set; }

    /// <inheritdoc />
    public bool HasDependentStreams
    {
        get
        {
            ThrowIfDisposed();
            lock (_dependentStreamIds)
            {
                return _dependentStreamIds.Count != 0;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<Guid> DependentStreamIds
    {
        get
        {
            ThrowIfDisposed();
            lock (_dependentStreamIds)
            {
                return _dependentStreamIds.Order().ToArray();
            }
        }
    }

    /// <inheritdoc />
    public override bool CanRead => !_disposed && _baseStream.CanRead && _differenceStream.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => !_disposed && _baseStream.CanSeek && _differenceStream.CanSeek;

    /// <inheritdoc />
    public override bool CanWrite => !_disposed && !_readOnly && _differenceStream.CanWrite;

    /// <inheritdoc />
    public bool CanReadAt => CanRead;

    /// <inheritdoc />
    public bool CanWriteAt => CanWrite;

    /// <inheritdoc />
    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            return Volatile.Read(ref _logicalLength);
        }
    }

    /// <inheritdoc />
    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return _position;
        }
        set
        {
            ThrowIfDisposed();
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _operationGate.Wait();
            try
            {
                _position = value;
            }
            finally
            {
                _operationGate.Release();
            }
        }
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        _operationGate.Wait();
        try
        {
            int read = ReadCore(buffer, _position);
            _position += read;
            return read;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int read = await ReadCoreAsync(buffer, _position, cancellationToken).ConfigureAwait(false);
            _position += read;
            return read;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public int ReadAt(Span<byte> buffer, long offset)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        _operationGate.Wait();
        try
        {
            return ReadCore(buffer, offset);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<int> ReadAtAsync(
        Memory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadCoreAsync(buffer, offset, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        _operationGate.Wait();
        try
        {
            WriteCore(buffer, _position);
            _position += buffer.Length;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteCoreAsync(buffer, _position, cancellationToken).ConfigureAwait(false);
            _position += buffer.Length;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public void WriteAt(ReadOnlySpan<byte> buffer, long offset)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        _operationGate.Wait();
        try
        {
            WriteCore(buffer, offset);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask WriteAtAsync(
        ReadOnlyMemory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteCoreAsync(buffer, offset, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenReadRangeAsync(
        long offset,
        long length,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        cancellationToken.ThrowIfCancellationRequested();
        long boundedLength = offset >= Length ? 0 : Math.Min(length, Length - offset);
        return ValueTask.FromResult<Stream>(new BoundedRandomAccessReadStream(this, offset, boundedLength));
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        _operationGate.Wait();
        try
        {
            long originValue = origin switch
            {
                SeekOrigin.Begin => 0,
                SeekOrigin.Current => _position,
                SeekOrigin.End => _logicalLength,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            long result = checked(originValue + offset);
            ArgumentOutOfRangeException.ThrowIfNegative(result);
            _position = result;
            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public override void SetLength(long value) =>
        throw new NotSupportedException("DifferencingDiskImage has an immutable virtual capacity.");

    /// <inheritdoc />
    public override void Flush()
    {
        ThrowIfDisposed();
        _operationGate.Wait();
        try
        {
            PhysicalBarrier();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PhysicalBarrierAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Deterministically masks an absolute logical range with zero.</summary>
    public void Trim(long offset, long length)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        ValidateTrimRange(offset, length);
        _operationGate.Wait();
        try
        {
            TrimCore(offset, length);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Asynchronously masks an absolute logical range with zero.</summary>
    public async ValueTask TrimAsync(
        long offset,
        long length,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        ValidateTrimRange(offset, length);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await TrimCoreAsync(offset, length, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public void RegisterDependentStream(Guid id)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        ValidateDependentId(id);
        _operationGate.Wait();
        try
        {
            if (_dependentStreamIds.Add(id))
            {
                AppendRegistryRecord(id, registered: true);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask RegisterDependentStreamAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RegisterDependentStream(id);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void UnregisterDependentStream(Guid id)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        ValidateDependentId(id);
        _operationGate.Wait();
        try
        {
            if (_dependentStreamIds.Remove(id))
            {
                AppendRegistryRecord(id, registered: false);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask UnregisterDependentStreamAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UnregisterDependentStream(id);
        return ValueTask.CompletedTask;
    }

    /// <summary>Estimates bytes reclaimable from superseded immutable metadata and payload.</summary>
    public long EstimateCompactionSavings()
    {
        ThrowIfDisposed();
        _operationGate.Wait();
        try
        {
            return EstimateCompactionSavingsCore();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Asynchronously estimates compaction savings.</summary>
    public ValueTask<long> EstimateCompactionSavingsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(EstimateCompactionSavings());
    }

    /// <summary>Compacts child storage.</summary>
    public long Compact(DynamicAllocationCompactionMode mode = DynamicAllocationCompactionMode.Fast)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        if (mode is not (DynamicAllocationCompactionMode.Fast or DynamicAllocationCompactionMode.Slow))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        _operationGate.Wait();
        try
        {
            return CompactCore(mode);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Asynchronously compacts child storage.</summary>
    public ValueTask<long> CompactAsync(
        DynamicAllocationCompactionMode mode = DynamicAllocationCompactionMode.Fast,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Compact(mode));
    }

    private long EstimateCompactionSavingsCore()
    {
        long livePayloadCount = _blocks.Values.Count(static block => block.PayloadOffset != 0);
        long liveStateCount = _blocks.Values.Count(static block => block.State != DifferenceBlockState.Inherited);
        long ideal = checked((1 + livePayloadCount + liveStateCount + _dependentStreamIds.Count) * _root.BlockSize);
        return Math.Max(0, _differenceStream.Length - ideal);
    }

    private long CompactCore(DynamicAllocationCompactionMode mode)
    {
        PhysicalBarrier();
        if (mode == DynamicAllocationCompactionMode.Slow)
        {
            ConvertZeroBlocksToErased();
            _logicalLength = RecomputeLogicalLength();
        }

        foreach (long logicalBlock in _blocks
            .Where(static item => item.Value.State == DifferenceBlockState.Inherited)
            .Select(static item => item.Key)
            .ToArray())
        {
            _blocks.Remove(logicalBlock);
        }

        HashSet<long> oldMetadataOffsets = CollectMetadataOffsets();
        int finalMetadataCount = checked(
            _blocks.Values.Count(static block => block.State != DifferenceBlockState.Inherited) +
            _dependentStreamIds.Count);
        if (oldMetadataOffsets.Count < finalMetadataCount)
        {
            throw Corruption("There are not enough current metadata blocks to rebuild a compact snapshot.");
        }

        long[] transitionalTargets = AllocateSnapshotTargets(finalMetadataCount);
        SnapshotTails transitional = WriteMetadataSnapshot(transitionalTargets);
        PhysicalBarrier();
        PublishRoot(_root with
        {
            Generation = checked(_root.Generation + 1),
            LogicalLength = _logicalLength,
            StateTailOffset = transitional.StateTailOffset,
            RegistryTailOffset = transitional.RegistryTailOffset,
        });

        long[] finalTargets = oldMetadataOffsets.Order().Take(finalMetadataCount).ToArray();
        PackPayloadBlocks(finalTargets, transitionalTargets);
        PhysicalBarrier();
        SnapshotTails final = WriteMetadataSnapshot(finalTargets);
        PhysicalBarrier();
        PublishRoot(_root with
        {
            Generation = checked(_root.Generation + 1),
            LogicalLength = _logicalLength,
            StateTailOffset = final.StateTailOffset,
            RegistryTailOffset = final.RegistryTailOffset,
        });

        long[] idealMetadataTargets = GetIdealMetadataTargets(finalMetadataCount);
        if (!finalTargets.SequenceEqual(idealMetadataTargets))
        {
            long[] secondTransitionalTargets = AllocateSnapshotTargets(finalMetadataCount);
            SnapshotTails secondTransitional = WriteMetadataSnapshot(secondTransitionalTargets);
            PhysicalBarrier();
            PublishRoot(_root with
            {
                Generation = checked(_root.Generation + 1),
                StateTailOffset = secondTransitional.StateTailOffset,
                RegistryTailOffset = secondTransitional.RegistryTailOffset,
            });

            SnapshotTails packedFinal = WriteMetadataSnapshot(idealMetadataTargets);
            PhysicalBarrier();
            PublishRoot(_root with
            {
                Generation = checked(_root.Generation + 1),
                StateTailOffset = packedFinal.StateTailOffset,
                RegistryTailOffset = packedFinal.RegistryTailOffset,
            });
            finalTargets = idealMetadataTargets;
        }

        long maximumOffset = _blocks.Values
            .Where(static block => block.PayloadOffset != 0)
            .Select(static block => block.PayloadOffset)
            .Concat(finalTargets)
            .DefaultIfEmpty(0)
            .Max();
        long targetLength = maximumOffset == 0
            ? _root.BlockSize
            : checked(maximumOffset + _root.BlockSize);
        try
        {
            _differenceStream.SetLength(targetLength);
            PhysicalBarrier();
            _nextAppendOffset = targetLength;
        }
        catch (NotSupportedException)
        {
            _nextAppendOffset = DifferencingFormat.AlignUp(_differenceStream.Length, _root.BlockSize);
        }

        return _differenceStream.Length;
    }

    private long[] GetIdealMetadataTargets(int count)
    {
        var payloadOffsets = _blocks.Values
            .Where(static block => block.PayloadOffset != 0)
            .Select(static block => block.PayloadOffset)
            .ToHashSet();
        var result = new long[count];
        long candidate = _root.BlockSize;
        int index = 0;
        while (index < count)
        {
            if (!payloadOffsets.Contains(candidate))
            {
                result[index++] = candidate;
            }

            candidate += _root.BlockSize;
        }

        return result;
    }

    private void ConvertZeroBlocksToErased()
    {
        foreach ((long logicalBlock, BlockState block) in _blocks.ToArray())
        {
            if (block.State is not (DifferenceBlockState.FullyPresent or DifferenceBlockState.PartiallyPresent) ||
                !IsLogicalBlockZero(logicalBlock))
            {
                continue;
            }

            block.State = DifferenceBlockState.Erased;
            block.PayloadOffset = 0;
            Array.Clear(block.Presence);
        }
    }

    private bool IsLogicalBlockZero(long logicalBlock)
    {
        int length = GetLogicalBlockLength(logicalBlock);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(length, 1024 * 1024));
        try
        {
            int completed = 0;
            while (completed < length)
            {
                int count = Math.Min(buffer.Length, length - completed);
                Span<byte> chunk = buffer.AsSpan(0, count);
                ReadBlockRange(logicalBlock, completed, chunk);
                if (chunk.IndexOfAnyExcept((byte)0) >= 0)
                {
                    return false;
                }

                completed += count;
            }

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private HashSet<long> CollectMetadataOffsets()
    {
        var result = new HashSet<long>();
        byte[] buffer = new byte[_root.BlockSize];
        long offset = _root.StateTailOffset;
        while (offset != 0)
        {
            ValidatePhysicalOffset(offset);
            if (!result.Add(offset))
            {
                throw Corruption("A metadata chain loops or overlaps another metadata chain.");
            }

            ReadDifferenceExactly(offset, buffer);
            if (!DifferencingFormat.TryReadStateRecord(buffer, out DifferenceBlockRecord record))
            {
                throw Corruption("A state record became invalid before compaction.");
            }

            offset = record.PreviousOffset;
        }

        offset = _root.RegistryTailOffset;
        while (offset != 0)
        {
            ValidatePhysicalOffset(offset);
            if (!result.Add(offset))
            {
                throw Corruption("A metadata chain loops or overlaps another metadata chain.");
            }

            ReadDifferenceExactly(offset, buffer);
            if (!DifferencingFormat.TryReadRegistryRecord(buffer, out long previous, out _, out _))
            {
                throw Corruption("A registry record became invalid before compaction.");
            }

            offset = previous;
        }

        return result;
    }

    private long[] AllocateSnapshotTargets(int count)
    {
        var result = new long[count];
        for (int index = 0; index < count; index++)
        {
            result[index] = AllocatePhysicalBlock();
        }

        return result;
    }

    private SnapshotTails WriteMetadataSnapshot(long[] targets)
    {
        int targetIndex = 0;
        long stateTail = 0;
        foreach ((long logicalBlock, BlockState block) in _blocks
            .Where(static item => item.Value.State != DifferenceBlockState.Inherited)
            .OrderBy(static item => item.Key))
        {
            long offset = targets[targetIndex++];
            byte[] buffer = new byte[_root.BlockSize];
            long batValue = DifferencingFormat.ComposeBatValue(block.PayloadOffset, block.State);
            DifferencingFormat.WriteStateRecord(
                buffer,
                new DifferenceBlockRecord(stateTail, logicalBlock, batValue, block.Presence));
            WriteDifferenceAt(offset, buffer);
            stateTail = offset;
        }

        long registryTail = 0;
        foreach (Guid id in _dependentStreamIds.Order())
        {
            long offset = targets[targetIndex++];
            byte[] buffer = new byte[_root.BlockSize];
            DifferencingFormat.WriteRegistryRecord(buffer, registryTail, id, registered: true);
            WriteDifferenceAt(offset, buffer);
            registryTail = offset;
        }

        if (targetIndex != targets.Length)
        {
            throw new InvalidOperationException("The compact metadata target count changed during compaction.");
        }

        return new SnapshotTails(stateTail, registryTail);
    }

    private void PackPayloadBlocks(
        IReadOnlyCollection<long> finalMetadataTargets,
        IReadOnlyCollection<long> transitionalMetadataTargets)
    {
        var owners = new SortedDictionary<long, BlockState>();
        foreach (BlockState block in _blocks.Values.Where(static block => block.PayloadOffset != 0))
        {
            if (!owners.TryAdd(block.PayloadOffset, block))
            {
                throw Corruption("Two logical blocks own the same payload during compaction.");
            }
        }

        var originalPayloadOffsets = owners.Keys.ToHashSet();
        var reserved = finalMetadataTargets.Concat(transitionalMetadataTargets).ToHashSet();
        long target = _root.BlockSize;
        while (owners.Count != 0)
        {
            if (reserved.Contains(target))
            {
                target += _root.BlockSize;
                continue;
            }

            if (owners.ContainsKey(target))
            {
                target += _root.BlockSize;
                continue;
            }

            if (originalPayloadOffsets.Contains(target))
            {
                target += _root.BlockSize;
                continue;
            }

            KeyValuePair<long, BlockState> last = owners.Last();
            if (last.Key <= target)
            {
                break;
            }

            CopyPhysicalBlock(last.Key, target);
            last.Value.PayloadOffset = target;
            owners.Remove(last.Key);
            owners.Add(target, last.Value);
            target += _root.BlockSize;
        }
    }

    private void CopyPhysicalBlock(long sourceOffset, long destinationOffset)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(_root.BlockSize, 1024 * 1024));
        try
        {
            int completed = 0;
            while (completed < _root.BlockSize)
            {
                int count = Math.Min(buffer.Length, _root.BlockSize - completed);
                Span<byte> chunk = buffer.AsSpan(0, count);
                ReadDifferenceExactly(sourceOffset + completed, chunk);
                WriteDifferenceAt(destinationOffset + completed, chunk);
                completed += count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!disposing || _disposed)
        {
            base.Dispose(disposing);
            return;
        }

        _disposed = true;
        _operationGate.Dispose();
        if (!_options.LeaveDifferenceOpen)
        {
            _differenceStream.Dispose();
        }

        if (!_options.LeaveBaseOpen)
        {
            _baseStream.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _operationGate.Dispose();
        if (!_options.LeaveDifferenceOpen)
        {
            await _differenceStream.DisposeAsync().ConfigureAwait(false);
        }

        if (!_options.LeaveBaseOpen)
        {
            await _baseStream.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void InitializeCreate(
        StreamIdentity baseIdentity,
        long virtualCapacity,
        int blockSize,
        string? parentPathHint)
    {
        ValidateStreamsForCreate(virtualCapacity, blockSize, parentPathHint);
        if (_options.NotifyBaseOnCreate &&
            (_baseStream is not IDependentStreamRegistry || !_baseStream.CanWrite))
        {
            throw new ArgumentException(
                "NotifyBaseOnCreate requires a writable base that exposes a dependent-stream registry.");
        }

        Guid id = Guid.NewGuid();
        Guid dataWriteId = Guid.NewGuid();
        _root = new DifferencingRoot(
            2,
            id,
            dataWriteId,
            baseIdentity.Id,
            baseIdentity.DataWriteId,
            DifferencingFormat.MajorVersion,
            DifferencingFormat.MinorVersion,
            blockSize,
            virtualCapacity,
            GetBaseRoundedLength(virtualCapacity, blockSize),
            0,
            0);

        byte[] sector = new byte[DifferencingFormat.SectorSize];
        byte[] hintBytes = parentPathHint is null ? [] : Encoding.UTF8.GetBytes(parentPathHint);
        var identity = new DifferencingIdentity(
            id,
            dataWriteId,
            baseIdentity.Id,
            baseIdentity.DataWriteId,
            DifferencingFormat.MajorVersion,
            DifferencingFormat.MinorVersion,
            blockSize,
            virtualCapacity);
        DifferencingFormat.WriteIdentifier(sector, identity, hintBytes);
        WriteDifferenceAt(DifferencingFormat.IdentifierOffset, sector);
        DifferencingFormat.WriteRoot(sector, _root with { Generation = 1 });
        WriteDifferenceAt(DifferencingFormat.RootAOffset, sector);
        DifferencingFormat.WriteRoot(sector, _root);
        WriteDifferenceAt(DifferencingFormat.RootBOffset, sector);

        byte[] zero = new byte[DifferencingFormat.SectorSize];
        WriteDifferenceAt(blockSize - zero.Length, zero);
        PhysicalBarrier();
        _logicalLength = _root.LogicalLength;
        _nextAppendOffset = blockSize;
        _readOnly = false;
        _dataWriteIdAdvanced = false;
        ParentPathHint = parentPathHint;

        if (_options.NotifyBaseOnCreate)
        {
            ((IDependentStreamRegistry)_baseStream).RegisterDependentStream(id);
        }
    }

    private static DifferencingDiskImageLocator ParseLocator(ReadOnlySpan<byte> identifierBuffer)
    {
        if (!DifferencingFormat.TryReadIdentifier(
            identifierBuffer,
            out DifferencingIdentity identity,
            out byte[] parentHint))
        {
            throw Corruption("The differencing identifier is invalid.");
        }

        if (identity.MajorVersion != DifferencingFormat.MajorVersion)
        {
            throw new NotSupportedException(
                $"Differencing format major version {identity.MajorVersion} is not supported.");
        }

        return new DifferencingDiskImageLocator(
            identity.BaseId,
            identity.BaseDataWriteId,
            identity.VirtualCapacity,
            identity.BlockSize,
            DecodeParentHint(parentHint));
    }

    private static string? DecodeParentHint(ReadOnlySpan<byte> parentHint)
    {
        if (parentHint.IsEmpty)
        {
            return null;
        }

        try
        {
            return StrictUtf8.GetString(parentHint);
        }
        catch (DecoderFallbackException exception)
        {
            throw Corruption("The parent path hint is not valid UTF-8.", exception);
        }
    }

    private void InitializeOpen(StreamIdentity currentBaseIdentity)
    {
        if (ReferenceEquals(_baseStream, _differenceStream))
        {
            throw new ArgumentException("Base and difference streams must be distinct.");
        }

        if (!_baseStream.CanRead || !_baseStream.CanSeek || !_differenceStream.CanRead || !_differenceStream.CanSeek)
        {
            throw new ArgumentException("Open requires distinct readable, seekable base and difference streams.");
        }

        if (_differenceStream.Length < DifferencingFormat.RootBOffset + DifferencingFormat.SectorSize)
        {
            throw Corruption("The difference stream is too short to contain its headers.");
        }

        byte[] identifierBuffer = new byte[DifferencingFormat.SectorSize];
        byte[] rootABuffer = new byte[DifferencingFormat.SectorSize];
        byte[] rootBBuffer = new byte[DifferencingFormat.SectorSize];
        ReadDifferenceExactly(DifferencingFormat.IdentifierOffset, identifierBuffer);
        ReadDifferenceExactly(DifferencingFormat.RootAOffset, rootABuffer);
        ReadDifferenceExactly(DifferencingFormat.RootBOffset, rootBBuffer);
        bool hasIdentifier = DifferencingFormat.TryReadIdentifier(identifierBuffer, out DifferencingIdentity identity, out byte[] hint);
        bool hasA = DifferencingFormat.TryReadRoot(rootABuffer, out DifferencingRoot rootA);
        bool hasB = DifferencingFormat.TryReadRoot(rootBBuffer, out DifferencingRoot rootB);
        if (!hasA && !hasB)
        {
            throw Corruption("Neither redundant differencing root is valid.");
        }

        if (hasA && hasB && rootA.Generation == rootB.Generation && rootA != rootB)
        {
            throw Corruption("Redundant roots have equal generations but different contents.");
        }

        _root = !hasB || (hasA && rootA.Generation > rootB.Generation) ? rootA : rootB;
        if (_root.MajorVersion != DifferencingFormat.MajorVersion)
        {
            throw new NotSupportedException($"Differencing format major version {_root.MajorVersion} is not supported.");
        }

        if (hasIdentifier &&
            (identity.Id != _root.Id || identity.BaseId != _root.BaseId ||
            identity.BaseDataWriteId != _root.BaseDataWriteId || identity.BlockSize != _root.BlockSize ||
            identity.VirtualCapacity != _root.VirtualCapacity))
        {
            throw Corruption("The identifier and current root disagree.");
        }

        if (currentBaseIdentity.Id != _root.BaseId || currentBaseIdentity.DataWriteId != _root.BaseDataWriteId)
        {
            throw new DifferencingDiskImageBaseMismatchException(
                "The supplied base identity or data generation does not match the child image.");
        }

        if (_baseStream is IVirtualDiskStream virtualDisk &&
            (virtualDisk.BlockSize != _root.BlockSize || virtualDisk.VirtualCapacity != _root.VirtualCapacity))
        {
            throw new DifferencingDiskImageBaseMismatchException(
                "The supplied base geometry does not match the child image.");
        }

        _readOnly = _options.ReadOnly || !_differenceStream.CanWrite ||
            _root.MinorVersion != DifferencingFormat.MinorVersion;
        HashSet<long> physicalOwners = LoadStateChain();
        LoadRegistryChain(physicalOwners);
        _logicalLength = RecomputeLogicalLength();
        _nextAppendOffset = DifferencingFormat.AlignUp(_differenceStream.Length, _root.BlockSize);
        _dataWriteIdAdvanced = false;
        ParentPathHint = hasIdentifier ? DecodeParentHint(hint) : null;
    }

    private void ValidateStreamsForCreate(long virtualCapacity, int blockSize, string? parentPathHint)
    {
        if (ReferenceEquals(_baseStream, _differenceStream))
        {
            throw new ArgumentException("Base and difference streams must be distinct.");
        }

        if (!_baseStream.CanRead || !_baseStream.CanSeek || !_differenceStream.CanRead ||
            !_differenceStream.CanWrite || !_differenceStream.CanSeek)
        {
            throw new ArgumentException("Create requires a readable, seekable base and an empty readable, writable, seekable difference stream.");
        }

        if (_differenceStream.Length != 0)
        {
            throw new ArgumentException("Create requires an empty difference stream.");
        }

        if (_options.ReadOnly)
        {
            throw new ArgumentException("A new child cannot be forced read-only.");
        }

        if (!DynamicAllocationFormat.IsValidBlockSize(blockSize))
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize));
        }

        if (!DynamicAllocationFormat.IsValidVirtualCapacity(virtualCapacity))
        {
            throw new ArgumentOutOfRangeException(
                nameof(virtualCapacity),
                "Virtual capacity must be positive and aligned to 4 KiB.");
        }

        if (_baseStream is IVirtualDiskStream virtualDisk &&
            (virtualDisk.BlockSize != blockSize || virtualDisk.VirtualCapacity != virtualCapacity))
        {
            throw new ArgumentException("The requested child geometry does not match the TeeForge base.");
        }

        if (parentPathHint is not null && Encoding.UTF8.GetByteCount(parentPathHint) >
            DifferencingFormat.SectorSize - DifferencingFormat.ParentHintOffset)
        {
            throw new ArgumentException("The UTF-8 parent path hint is too long.", nameof(parentPathHint));
        }
    }

    private HashSet<long> LoadStateChain()
    {
        _blocks.Clear();
        var visitedRecords = new HashSet<long>();
        var physicalOwners = new HashSet<long> { 0 };
        long offset = _root.StateTailOffset;
        byte[] recordBuffer = new byte[_root.BlockSize];
        while (offset != 0)
        {
            ValidatePhysicalOffset(offset);
            if (!visitedRecords.Add(offset) || !physicalOwners.Add(offset))
            {
                throw Corruption("The state-record chain loops or has duplicate physical ownership.");
            }

            ReadDifferenceExactly(offset, recordBuffer);
            if (!DifferencingFormat.TryReadStateRecord(recordBuffer, out DifferenceBlockRecord record) ||
                record.LogicalBlock >= GetLogicalBlockCount() ||
                !DifferencingFormat.IsValidBatValue(record.BatValue, _root.BlockSize))
            {
                throw Corruption("A differencing state record is invalid.");
            }

            DifferenceBlockState state = DifferencingFormat.GetBatState(record.BatValue);
            long payloadOffset = DifferencingFormat.GetBatPayloadOffset(record.BatValue);
            if (payloadOffset != 0)
            {
                ValidatePhysicalOffset(payloadOffset);
            }

            if (!_blocks.ContainsKey(record.LogicalBlock))
            {
                _blocks.Add(record.LogicalBlock, new BlockState(state, payloadOffset, record.Presence));
                if (payloadOffset != 0 && !physicalOwners.Add(payloadOffset))
                {
                    throw Corruption("Two live objects own the same physical block.");
                }
            }

            offset = record.PreviousOffset;
        }

        return physicalOwners;
    }

    private void LoadRegistryChain(HashSet<long> physicalOwners)
    {
        _dependentStreamIds.Clear();
        var decided = new HashSet<Guid>();
        var visited = new HashSet<long>();
        long offset = _root.RegistryTailOffset;
        byte[] buffer = new byte[_root.BlockSize];
        while (offset != 0)
        {
            ValidatePhysicalOffset(offset);
            if (!visited.Add(offset) || !physicalOwners.Add(offset))
            {
                throw Corruption("The dependent-registry chain loops or overlaps another live object.");
            }

            ReadDifferenceExactly(offset, buffer);
            if (!DifferencingFormat.TryReadRegistryRecord(buffer, out long previous, out Guid id, out bool registered))
            {
                throw Corruption("A dependent-registry record is invalid.");
            }

            if (decided.Add(id) && registered)
            {
                _dependentStreamIds.Add(id);
            }

            offset = previous;
        }
    }

    private int ReadCore(Span<byte> destination, long offset)
    {
        if (destination.IsEmpty || offset >= _logicalLength)
        {
            return 0;
        }

        int total = (int)Math.Min(destination.Length, _logicalLength - offset);
        int completed = 0;
        while (completed < total)
        {
            long logicalPosition = offset + completed;
            long logicalBlock = logicalPosition / _root.BlockSize;
            int blockOffset = (int)(logicalPosition & (_root.BlockSize - 1L));
            int count = Math.Min(total - completed, GetLogicalBlockLength(logicalBlock) - blockOffset);
            ReadBlockRange(logicalBlock, blockOffset, destination.Slice(completed, count));
            completed += count;
        }

        return total;
    }

    private async ValueTask<int> ReadCoreAsync(
        Memory<byte> destination,
        long offset,
        CancellationToken cancellationToken)
    {
        if (destination.IsEmpty || offset >= _logicalLength)
        {
            return 0;
        }

        int total = (int)Math.Min(destination.Length, _logicalLength - offset);
        int completed = 0;
        while (completed < total)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long logicalPosition = offset + completed;
            long logicalBlock = logicalPosition / _root.BlockSize;
            int blockOffset = (int)(logicalPosition & (_root.BlockSize - 1L));
            int count = Math.Min(total - completed, GetLogicalBlockLength(logicalBlock) - blockOffset);
            await ReadBlockRangeAsync(
                logicalBlock,
                blockOffset,
                destination.Slice(completed, count),
                cancellationToken).ConfigureAwait(false);
            completed += count;
        }

        return total;
    }

    private void ReadBlockRange(long logicalBlock, int blockOffset, Span<byte> destination)
    {
        BlockState block = GetBlock(logicalBlock);
        if (block.State == DifferenceBlockState.Erased)
        {
            destination.Clear();
            return;
        }

        if (block.State == DifferenceBlockState.FullyPresent)
        {
            ReadDifferenceExactly(block.PayloadOffset + blockOffset, destination);
            return;
        }

        if (block.State == DifferenceBlockState.Inherited)
        {
            ReadBaseOrZero((logicalBlock * (long)_root.BlockSize) + blockOffset, destination);
            return;
        }

        int completed = 0;
        while (completed < destination.Length)
        {
            int local = blockOffset + completed;
            int grain = local / DifferencingFormat.SectorSize;
            int grainOffset = local & (DifferencingFormat.SectorSize - 1);
            int count = Math.Min(destination.Length - completed, DifferencingFormat.SectorSize - grainOffset);
            Span<byte> chunk = destination.Slice(completed, count);
            if (IsPresenceSet(block.Presence, grain))
            {
                ReadDifferenceExactly(block.PayloadOffset + local, chunk);
            }
            else
            {
                ReadBaseOrZero((logicalBlock * (long)_root.BlockSize) + local, chunk);
            }

            completed += count;
        }
    }

    private async ValueTask ReadBlockRangeAsync(
        long logicalBlock,
        int blockOffset,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        BlockState block = GetBlock(logicalBlock);
        if (block.State == DifferenceBlockState.Erased)
        {
            destination.Span.Clear();
            return;
        }

        if (block.State == DifferenceBlockState.FullyPresent)
        {
            await ReadDifferenceExactlyAsync(block.PayloadOffset + blockOffset, destination, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (block.State == DifferenceBlockState.Inherited)
        {
            await ReadBaseOrZeroAsync(
                (logicalBlock * (long)_root.BlockSize) + blockOffset,
                destination,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        int completed = 0;
        while (completed < destination.Length)
        {
            int local = blockOffset + completed;
            int grain = local / DifferencingFormat.SectorSize;
            int grainOffset = local & (DifferencingFormat.SectorSize - 1);
            int count = Math.Min(destination.Length - completed, DifferencingFormat.SectorSize - grainOffset);
            Memory<byte> chunk = destination.Slice(completed, count);
            if (IsPresenceSet(block.Presence, grain))
            {
                await ReadDifferenceExactlyAsync(block.PayloadOffset + local, chunk, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ReadBaseOrZeroAsync(
                    (logicalBlock * (long)_root.BlockSize) + local,
                    chunk,
                    cancellationToken).ConfigureAwait(false);
            }

            completed += count;
        }
    }

    private void WriteCore(ReadOnlySpan<byte> source, long offset)
    {
        if (source.IsEmpty)
        {
            return;
        }

        ValidateWriteRange(offset, source.Length);
        EnsureDataWriteId();
        int completed = 0;
        while (completed < source.Length)
        {
            long logicalPosition = offset + completed;
            long logicalBlock = logicalPosition / _root.BlockSize;
            int blockOffset = (int)(logicalPosition & (_root.BlockSize - 1L));
            int blockLength = GetLogicalBlockLength(logicalBlock);
            int count = Math.Min(source.Length - completed, blockLength - blockOffset);
            WriteBlockRange(logicalBlock, blockOffset, source.Slice(completed, count));
            completed += count;
        }
    }

    private async ValueTask WriteCoreAsync(
        ReadOnlyMemory<byte> source,
        long offset,
        CancellationToken cancellationToken)
    {
        if (source.IsEmpty)
        {
            return;
        }

        ValidateWriteRange(offset, source.Length);
        await EnsureDataWriteIdAsync(cancellationToken).ConfigureAwait(false);
        int completed = 0;
        while (completed < source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long logicalPosition = offset + completed;
            long logicalBlock = logicalPosition / _root.BlockSize;
            int blockOffset = (int)(logicalPosition & (_root.BlockSize - 1L));
            int blockLength = GetLogicalBlockLength(logicalBlock);
            int count = Math.Min(source.Length - completed, blockLength - blockOffset);
            await WriteBlockRangeAsync(
                logicalBlock,
                blockOffset,
                source.Slice(completed, count),
                cancellationToken).ConfigureAwait(false);
            completed += count;
        }
    }

    private void WriteBlockRange(long logicalBlock, int blockOffset, ReadOnlySpan<byte> source)
    {
        BlockState block = GetBlock(logicalBlock);
        int blockLength = GetLogicalBlockLength(logicalBlock);
        bool wholeBlock = blockOffset == 0 && source.Length == blockLength;

        if (block.State == DifferenceBlockState.FullyPresent)
        {
            WriteDifferenceAt(block.PayloadOffset + blockOffset, source);
            ExtendLengthForBlock(logicalBlock);
            return;
        }

        if (block.State == DifferenceBlockState.Erased)
        {
            block.PayloadOffset = AllocateZeroedPhysicalBlock();
            block.State = DifferenceBlockState.FullyPresent;
            Array.Clear(block.Presence);
            WriteDifferenceAt(block.PayloadOffset + blockOffset, source);
            PhysicalBarrier();
            AppendStateRecord(logicalBlock, block);
            return;
        }

        if (block.PayloadOffset == 0)
        {
            block.PayloadOffset = AllocateZeroedPhysicalBlock();
        }

        if (wholeBlock)
        {
            WriteDifferenceAt(block.PayloadOffset, source);
            if (blockLength < _root.BlockSize)
            {
                ZeroDifferenceRange(block.PayloadOffset + blockLength, _root.BlockSize - blockLength);
            }

            block.State = DifferenceBlockState.FullyPresent;
            Array.Clear(block.Presence);
            PhysicalBarrier();
            AppendStateRecord(logicalBlock, block);
            return;
        }

        int completed = 0;
        byte[] grainBuffer = ArrayPool<byte>.Shared.Rent(DifferencingFormat.SectorSize);
        try
        {
            while (completed < source.Length)
            {
                int local = blockOffset + completed;
                int grain = local / DifferencingFormat.SectorSize;
                int grainStart = grain * DifferencingFormat.SectorSize;
                int withinGrain = local - grainStart;
                int grainLogicalLength = Math.Min(DifferencingFormat.SectorSize, blockLength - grainStart);
                int count = Math.Min(source.Length - completed, grainLogicalLength - withinGrain);
                Span<byte> grainSpan = grainBuffer.AsSpan(0, DifferencingFormat.SectorSize);
                if (IsPresenceSet(block.Presence, grain))
                {
                    ReadDifferenceExactly(block.PayloadOffset + grainStart, grainSpan);
                }
                else
                {
                    ReadBaseOrZero((logicalBlock * (long)_root.BlockSize) + grainStart, grainSpan[..grainLogicalLength]);
                    grainSpan[grainLogicalLength..].Clear();
                }

                source.Slice(completed, count).CopyTo(grainSpan.Slice(withinGrain, count));
                WriteDifferenceAt(block.PayloadOffset + grainStart, grainSpan);
                SetPresence(block.Presence, grain);
                completed += count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(grainBuffer);
        }

        block.State = AreAllLogicalGrainsPresent(block.Presence, blockLength)
            ? DifferenceBlockState.FullyPresent
            : DifferenceBlockState.PartiallyPresent;
        PhysicalBarrier();
        AppendStateRecord(logicalBlock, block);
    }

    private async ValueTask WriteBlockRangeAsync(
        long logicalBlock,
        int blockOffset,
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken)
    {
        BlockState block = GetBlock(logicalBlock);
        int blockLength = GetLogicalBlockLength(logicalBlock);
        bool wholeBlock = blockOffset == 0 && source.Length == blockLength;

        if (block.State == DifferenceBlockState.FullyPresent)
        {
            await WriteDifferenceAtAsync(block.PayloadOffset + blockOffset, source, cancellationToken).ConfigureAwait(false);
            ExtendLengthForBlock(logicalBlock);
            return;
        }

        if (block.State == DifferenceBlockState.Erased)
        {
            block.PayloadOffset = await AllocateZeroedPhysicalBlockAsync(cancellationToken).ConfigureAwait(false);
            block.State = DifferenceBlockState.FullyPresent;
            Array.Clear(block.Presence);
            await WriteDifferenceAtAsync(block.PayloadOffset + blockOffset, source, cancellationToken).ConfigureAwait(false);
            await PhysicalBarrierAsync(cancellationToken).ConfigureAwait(false);
            await AppendStateRecordAsync(logicalBlock, block, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (block.PayloadOffset == 0)
        {
            block.PayloadOffset = await AllocateZeroedPhysicalBlockAsync(cancellationToken).ConfigureAwait(false);
        }

        if (wholeBlock)
        {
            await WriteDifferenceAtAsync(block.PayloadOffset, source, cancellationToken).ConfigureAwait(false);
            if (blockLength < _root.BlockSize)
            {
                await ZeroDifferenceRangeAsync(
                    block.PayloadOffset + blockLength,
                    _root.BlockSize - blockLength,
                    cancellationToken).ConfigureAwait(false);
            }

            block.State = DifferenceBlockState.FullyPresent;
            Array.Clear(block.Presence);
            await PhysicalBarrierAsync(cancellationToken).ConfigureAwait(false);
            await AppendStateRecordAsync(logicalBlock, block, cancellationToken).ConfigureAwait(false);
            return;
        }

        int completed = 0;
        byte[] grainBuffer = ArrayPool<byte>.Shared.Rent(DifferencingFormat.SectorSize);
        try
        {
            while (completed < source.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int local = blockOffset + completed;
                int grain = local / DifferencingFormat.SectorSize;
                int grainStart = grain * DifferencingFormat.SectorSize;
                int withinGrain = local - grainStart;
                int grainLogicalLength = Math.Min(DifferencingFormat.SectorSize, blockLength - grainStart);
                int count = Math.Min(source.Length - completed, grainLogicalLength - withinGrain);
                Memory<byte> grainMemory = grainBuffer.AsMemory(0, DifferencingFormat.SectorSize);
                if (IsPresenceSet(block.Presence, grain))
                {
                    await ReadDifferenceExactlyAsync(
                        block.PayloadOffset + grainStart,
                        grainMemory,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await ReadBaseOrZeroAsync(
                        (logicalBlock * (long)_root.BlockSize) + grainStart,
                        grainMemory[..grainLogicalLength],
                        cancellationToken).ConfigureAwait(false);
                    grainMemory.Span[grainLogicalLength..].Clear();
                }

                source.Span.Slice(completed, count).CopyTo(grainMemory.Span.Slice(withinGrain, count));
                await WriteDifferenceAtAsync(
                    block.PayloadOffset + grainStart,
                    grainMemory,
                    cancellationToken).ConfigureAwait(false);
                SetPresence(block.Presence, grain);
                completed += count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(grainBuffer);
        }

        block.State = AreAllLogicalGrainsPresent(block.Presence, blockLength)
            ? DifferenceBlockState.FullyPresent
            : DifferenceBlockState.PartiallyPresent;
        await PhysicalBarrierAsync(cancellationToken).ConfigureAwait(false);
        await AppendStateRecordAsync(logicalBlock, block, cancellationToken).ConfigureAwait(false);
    }

    private void TrimCore(long offset, long length)
    {
        if (length == 0)
        {
            return;
        }

        EnsureDataWriteId();
        long end = offset + length;
        long cursor = offset;
        while (cursor < end)
        {
            long logicalBlock = cursor / _root.BlockSize;
            int blockOffset = (int)(cursor & (_root.BlockSize - 1L));
            int blockLength = GetLogicalBlockLength(logicalBlock);
            int count = (int)Math.Min(end - cursor, blockLength - blockOffset);
            BlockState block = GetBlock(logicalBlock);
            if (blockOffset == 0 && count == blockLength)
            {
                block.State = DifferenceBlockState.Erased;
                block.PayloadOffset = 0;
                Array.Clear(block.Presence);
                AppendStateRecord(logicalBlock, block);
            }
            else if (block.State != DifferenceBlockState.Erased)
            {
                ZeroBlockRange(logicalBlock, blockOffset, count);
            }

            cursor += count;
        }

        _logicalLength = RecomputeLogicalLength();
        if (_root.LogicalLength != _logicalLength)
        {
            PublishRoot(_root with
            {
                Generation = checked(_root.Generation + 1),
                LogicalLength = _logicalLength,
            });
        }
    }

    private async ValueTask TrimCoreAsync(long offset, long length, CancellationToken cancellationToken)
    {
        if (length == 0)
        {
            return;
        }

        await EnsureDataWriteIdAsync(cancellationToken).ConfigureAwait(false);
        long end = offset + length;
        long cursor = offset;
        while (cursor < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long logicalBlock = cursor / _root.BlockSize;
            int blockOffset = (int)(cursor & (_root.BlockSize - 1L));
            int blockLength = GetLogicalBlockLength(logicalBlock);
            int count = (int)Math.Min(end - cursor, blockLength - blockOffset);
            BlockState block = GetBlock(logicalBlock);
            if (blockOffset == 0 && count == blockLength)
            {
                block.State = DifferenceBlockState.Erased;
                block.PayloadOffset = 0;
                Array.Clear(block.Presence);
                await AppendStateRecordAsync(logicalBlock, block, cancellationToken).ConfigureAwait(false);
            }
            else if (block.State != DifferenceBlockState.Erased)
            {
                await ZeroBlockRangeAsync(logicalBlock, blockOffset, count, cancellationToken).ConfigureAwait(false);
            }

            cursor += count;
        }

        _logicalLength = RecomputeLogicalLength();
        if (_root.LogicalLength != _logicalLength)
        {
            await PublishRootAsync(_root with
            {
                Generation = checked(_root.Generation + 1),
                LogicalLength = _logicalLength,
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ZeroBlockRange(long logicalBlock, int blockOffset, int count)
    {
        BlockState block = GetBlock(logicalBlock);
        if (block.State == DifferenceBlockState.FullyPresent)
        {
            ZeroDifferenceRange(block.PayloadOffset + blockOffset, count);
            return;
        }

        if (block.PayloadOffset == 0)
        {
            block.PayloadOffset = AllocateZeroedPhysicalBlock();
        }

        int blockLength = GetLogicalBlockLength(logicalBlock);
        int completed = 0;
        byte[] grainBuffer = ArrayPool<byte>.Shared.Rent(DifferencingFormat.SectorSize);
        try
        {
            while (completed < count)
            {
                int local = blockOffset + completed;
                int grain = local / DifferencingFormat.SectorSize;
                int grainStart = grain * DifferencingFormat.SectorSize;
                int withinGrain = local - grainStart;
                int grainLogicalLength = Math.Min(DifferencingFormat.SectorSize, blockLength - grainStart);
                int length = Math.Min(count - completed, grainLogicalLength - withinGrain);
                Span<byte> grainSpan = grainBuffer.AsSpan(0, DifferencingFormat.SectorSize);
                if (IsPresenceSet(block.Presence, grain))
                {
                    ReadDifferenceExactly(block.PayloadOffset + grainStart, grainSpan);
                }
                else
                {
                    ReadBaseOrZero(
                        (logicalBlock * (long)_root.BlockSize) + grainStart,
                        grainSpan[..grainLogicalLength]);
                    grainSpan[grainLogicalLength..].Clear();
                }

                grainSpan.Slice(withinGrain, length).Clear();
                WriteDifferenceAt(block.PayloadOffset + grainStart, grainSpan);
                SetPresence(block.Presence, grain);
                completed += length;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(grainBuffer);
        }

        block.State = AreAllLogicalGrainsPresent(block.Presence, blockLength)
            ? DifferenceBlockState.FullyPresent
            : DifferenceBlockState.PartiallyPresent;
        PhysicalBarrier();
        AppendStateRecord(logicalBlock, block);
    }

    private async ValueTask ZeroBlockRangeAsync(
        long logicalBlock,
        int blockOffset,
        int count,
        CancellationToken cancellationToken)
    {
        BlockState block = GetBlock(logicalBlock);
        if (block.State == DifferenceBlockState.FullyPresent)
        {
            await ZeroDifferenceRangeAsync(block.PayloadOffset + blockOffset, count, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (block.PayloadOffset == 0)
        {
            block.PayloadOffset = await AllocateZeroedPhysicalBlockAsync(cancellationToken).ConfigureAwait(false);
        }

        int blockLength = GetLogicalBlockLength(logicalBlock);
        int completed = 0;
        byte[] grainBuffer = ArrayPool<byte>.Shared.Rent(DifferencingFormat.SectorSize);
        try
        {
            while (completed < count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int local = blockOffset + completed;
                int grain = local / DifferencingFormat.SectorSize;
                int grainStart = grain * DifferencingFormat.SectorSize;
                int withinGrain = local - grainStart;
                int grainLogicalLength = Math.Min(DifferencingFormat.SectorSize, blockLength - grainStart);
                int length = Math.Min(count - completed, grainLogicalLength - withinGrain);
                Memory<byte> grainMemory = grainBuffer.AsMemory(0, DifferencingFormat.SectorSize);
                if (IsPresenceSet(block.Presence, grain))
                {
                    await ReadDifferenceExactlyAsync(
                        block.PayloadOffset + grainStart,
                        grainMemory,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await ReadBaseOrZeroAsync(
                        (logicalBlock * (long)_root.BlockSize) + grainStart,
                        grainMemory[..grainLogicalLength],
                        cancellationToken).ConfigureAwait(false);
                    grainMemory.Span[grainLogicalLength..].Clear();
                }

                grainMemory.Span.Slice(withinGrain, length).Clear();
                await WriteDifferenceAtAsync(
                    block.PayloadOffset + grainStart,
                    grainMemory,
                    cancellationToken).ConfigureAwait(false);
                SetPresence(block.Presence, grain);
                completed += length;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(grainBuffer);
        }

        block.State = AreAllLogicalGrainsPresent(block.Presence, blockLength)
            ? DifferenceBlockState.FullyPresent
            : DifferenceBlockState.PartiallyPresent;
        await PhysicalBarrierAsync(cancellationToken).ConfigureAwait(false);
        await AppendStateRecordAsync(logicalBlock, block, cancellationToken).ConfigureAwait(false);
    }

    private void AppendStateRecord(long logicalBlock, BlockState block)
    {
        long recordOffset = AllocatePhysicalBlock();
        byte[] buffer = new byte[_root.BlockSize];
        long batValue = DifferencingFormat.ComposeBatValue(block.PayloadOffset, block.State);
        DifferencingFormat.WriteStateRecord(
            buffer,
            new DifferenceBlockRecord(_root.StateTailOffset, logicalBlock, batValue, block.Presence));
        WriteDifferenceAt(recordOffset, buffer);
        PhysicalBarrier();
        ExtendLengthForBlock(logicalBlock);
        PublishRoot(_root with
        {
            Generation = checked(_root.Generation + 1),
            LogicalLength = _logicalLength,
            StateTailOffset = recordOffset,
        });
    }

    private async ValueTask AppendStateRecordAsync(
        long logicalBlock,
        BlockState block,
        CancellationToken cancellationToken)
    {
        long recordOffset = AllocatePhysicalBlock();
        byte[] buffer = new byte[_root.BlockSize];
        long batValue = DifferencingFormat.ComposeBatValue(block.PayloadOffset, block.State);
        DifferencingFormat.WriteStateRecord(
            buffer,
            new DifferenceBlockRecord(_root.StateTailOffset, logicalBlock, batValue, block.Presence));
        await WriteDifferenceAtAsync(recordOffset, buffer, cancellationToken).ConfigureAwait(false);
        await PhysicalBarrierAsync(cancellationToken).ConfigureAwait(false);
        ExtendLengthForBlock(logicalBlock);
        await PublishRootAsync(_root with
        {
            Generation = checked(_root.Generation + 1),
            LogicalLength = _logicalLength,
            StateTailOffset = recordOffset,
        }, cancellationToken).ConfigureAwait(false);
    }

    private void AppendRegistryRecord(Guid id, bool registered)
    {
        long offset = AllocatePhysicalBlock();
        byte[] buffer = new byte[_root.BlockSize];
        DifferencingFormat.WriteRegistryRecord(buffer, _root.RegistryTailOffset, id, registered);
        WriteDifferenceAt(offset, buffer);
        PhysicalBarrier();
        PublishRoot(_root with
        {
            Generation = checked(_root.Generation + 1),
            RegistryTailOffset = offset,
        });
    }

    private void EnsureDataWriteId()
    {
        if (_dataWriteIdAdvanced)
        {
            return;
        }

        PublishRoot(_root with
        {
            Generation = checked(_root.Generation + 1),
            DataWriteId = Guid.NewGuid(),
        });
        _dataWriteIdAdvanced = true;
    }

    private async ValueTask EnsureDataWriteIdAsync(CancellationToken cancellationToken)
    {
        if (_dataWriteIdAdvanced)
        {
            return;
        }

        await PublishRootAsync(_root with
        {
            Generation = checked(_root.Generation + 1),
            DataWriteId = Guid.NewGuid(),
        }, cancellationToken).ConfigureAwait(false);
        _dataWriteIdAdvanced = true;
    }

    private void PublishRoot(DifferencingRoot root)
    {
        byte[] sector = new byte[DifferencingFormat.SectorSize];
        DifferencingFormat.WriteRoot(sector, root);
        int offset = (root.Generation & 1UL) == 0
            ? DifferencingFormat.RootBOffset
            : DifferencingFormat.RootAOffset;
        WriteDifferenceAt(offset, sector);
        PhysicalBarrier();
        _root = root;
    }

    private async ValueTask PublishRootAsync(DifferencingRoot root, CancellationToken cancellationToken)
    {
        byte[] sector = new byte[DifferencingFormat.SectorSize];
        DifferencingFormat.WriteRoot(sector, root);
        int offset = (root.Generation & 1UL) == 0
            ? DifferencingFormat.RootBOffset
            : DifferencingFormat.RootAOffset;
        await WriteDifferenceAtAsync(offset, sector, cancellationToken).ConfigureAwait(false);
        await PhysicalBarrierAsync(cancellationToken).ConfigureAwait(false);
        _root = root;
    }

    private BlockState GetBlock(long logicalBlock)
    {
        if (_blocks.TryGetValue(logicalBlock, out BlockState? block))
        {
            return block;
        }

        block = new BlockState(
            DifferenceBlockState.Inherited,
            0,
            new byte[DifferencingFormat.GetPresenceByteCount(_root.BlockSize)]);
        _blocks.Add(logicalBlock, block);
        return block;
    }

    private long AllocatePhysicalBlock()
    {
        long offset = _nextAppendOffset;
        _nextAppendOffset = checked(offset + _root.BlockSize);
        return offset;
    }

    private long AllocateZeroedPhysicalBlock()
    {
        long offset = AllocatePhysicalBlock();
        ZeroDifferenceRange(offset, _root.BlockSize);
        return offset;
    }

    private async ValueTask<long> AllocateZeroedPhysicalBlockAsync(CancellationToken cancellationToken)
    {
        long offset = AllocatePhysicalBlock();
        await ZeroDifferenceRangeAsync(offset, _root.BlockSize, cancellationToken).ConfigureAwait(false);
        return offset;
    }

    private void ZeroDifferenceRange(long offset, long length)
    {
        byte[] zeros = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, 64 * 1024));
        Array.Clear(zeros);
        try
        {
            while (length > 0)
            {
                int count = (int)Math.Min(length, zeros.Length);
                WriteDifferenceAt(offset, zeros.AsSpan(0, count));
                offset += count;
                length -= count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(zeros);
        }
    }

    private async ValueTask ZeroDifferenceRangeAsync(
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        byte[] zeros = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, 64 * 1024));
        Array.Clear(zeros);
        try
        {
            while (length > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(length, zeros.Length);
                await WriteDifferenceAtAsync(
                    offset,
                    zeros.AsMemory(0, count),
                    cancellationToken).ConfigureAwait(false);
                offset += count;
                length -= count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(zeros);
        }
    }

    private void ReadBaseOrZero(long offset, Span<byte> destination)
    {
        destination.Clear();
        if (offset >= _baseStream.Length)
        {
            return;
        }

        int requested = (int)Math.Min(destination.Length, _baseStream.Length - offset);
        Span<byte> remaining = destination[..requested];
        while (!remaining.IsEmpty)
        {
            int read = ReadBaseAt(offset, remaining);
            if (read == 0)
            {
                break;
            }

            offset += read;
            remaining = remaining[read..];
        }
    }

    private async ValueTask ReadBaseOrZeroAsync(
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        destination.Span.Clear();
        if (offset >= _baseStream.Length)
        {
            return;
        }

        int requested = (int)Math.Min(destination.Length, _baseStream.Length - offset);
        Memory<byte> remaining = destination[..requested];
        while (!remaining.IsEmpty)
        {
            int read = await ReadBaseAtAsync(offset, remaining, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
            remaining = remaining[read..];
        }
    }

    private int ReadBaseAt(long offset, Span<byte> destination)
    {
        if (_baseRandomAccess is { CanReadAt: true } randomAccess)
        {
            return randomAccess.ReadAt(destination, offset);
        }

        _baseStream.Position = offset;
        return _baseStream.Read(destination);
    }

    private ValueTask<int> ReadBaseAtAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (_baseRandomAccess is { CanReadAt: true } randomAccess)
        {
            return randomAccess.ReadAtAsync(destination, offset, cancellationToken);
        }

        _baseStream.Position = offset;
        return _baseStream.ReadAsync(destination, cancellationToken);
    }

    private void ReadDifferenceExactly(long offset, Span<byte> destination)
    {
        if (_differenceRandomAccess is { CanReadAt: true } randomAccess)
        {
            while (!destination.IsEmpty)
            {
                int read = randomAccess.ReadAt(destination, offset);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                offset += read;
                destination = destination[read..];
            }

            return;
        }

        _differenceStream.Position = offset;
        _differenceStream.ReadExactly(destination);
    }

    private async ValueTask ReadDifferenceExactlyAsync(
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        if (_differenceRandomAccess is { CanReadAt: true } randomAccess)
        {
            while (!destination.IsEmpty)
            {
                int read = await randomAccess.ReadAtAsync(destination, offset, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                offset += read;
                destination = destination[read..];
            }

            return;
        }

        _differenceStream.Position = offset;
        await _differenceStream.ReadExactlyAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private void WriteDifferenceAt(long offset, ReadOnlySpan<byte> source)
    {
        if (_differenceRandomAccess is { CanWriteAt: true } randomAccess)
        {
            randomAccess.WriteAt(source, offset);
            return;
        }

        _differenceStream.Position = offset;
        _differenceStream.Write(source);
    }

    private ValueTask WriteDifferenceAtAsync(
        long offset,
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken)
    {
        if (_differenceRandomAccess is { CanWriteAt: true } randomAccess)
        {
            return randomAccess.WriteAtAsync(source, offset, cancellationToken);
        }

        _differenceStream.Position = offset;
        return _differenceStream.WriteAsync(source, cancellationToken);
    }

    private void PhysicalBarrier()
    {
        if (_differenceStream is FileStream fileStream)
        {
            fileStream.Flush(flushToDisk: true);
        }
        else
        {
            _differenceStream.Flush();
        }
    }

    private async ValueTask PhysicalBarrierAsync(CancellationToken cancellationToken)
    {
        await _differenceStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (_differenceStream is FileStream fileStream)
        {
            System.IO.RandomAccess.FlushToDisk(fileStream.SafeFileHandle);
        }
    }

    private long RecomputeLogicalLength()
    {
        long blockCount = GetLogicalBlockCount();
        long baseLiveBlocks = Math.Min(blockCount, (_baseStream.Length + _root.BlockSize - 1) / _root.BlockSize);
        for (long block = blockCount - 1; block >= 0; block--)
        {
            if (_blocks.TryGetValue(block, out BlockState? state))
            {
                if (state.State is DifferenceBlockState.FullyPresent or DifferenceBlockState.PartiallyPresent)
                {
                    return GetLogicalBlockEnd(block);
                }

                if (state.State == DifferenceBlockState.Erased)
                {
                    continue;
                }
            }

            if (block < baseLiveBlocks)
            {
                return GetLogicalBlockEnd(block);
            }
        }

        return 0;
    }

    private long GetBaseRoundedLength(long capacity, int blockSize)
    {
        if (_baseStream.Length == 0)
        {
            return 0;
        }

        long rounded = Math.Min(capacity, DifferencingFormat.AlignUp(_baseStream.Length, blockSize));
        return rounded;
    }

    private long GetLogicalBlockCount() =>
        (_root.VirtualCapacity + _root.BlockSize - 1) / _root.BlockSize;

    private int GetLogicalBlockLength(long logicalBlock)
    {
        long start = checked(logicalBlock * (long)_root.BlockSize);
        return (int)Math.Min(_root.BlockSize, _root.VirtualCapacity - start);
    }

    private long GetLogicalBlockEnd(long logicalBlock) =>
        Math.Min(_root.VirtualCapacity, checked((logicalBlock + 1) * (long)_root.BlockSize));

    private void ExtendLengthForBlock(long logicalBlock)
    {
        long end = GetLogicalBlockEnd(logicalBlock);
        if (end > _logicalLength)
        {
            _logicalLength = end;
        }
    }

    private void ValidatePhysicalOffset(long offset)
    {
        if (offset < _root.BlockSize || (offset & (_root.BlockSize - 1L)) != 0 ||
            offset > _differenceStream.Length - _root.BlockSize)
        {
            throw Corruption("A physical block offset is invalid or truncated.");
        }
    }

    private void ValidateWriteRange(long offset, int length)
    {
        if (offset < 0 || offset > _root.VirtualCapacity || length > _root.VirtualCapacity - offset)
        {
            throw new IOException("The write would exceed virtual capacity.");
        }
    }

    private void ValidateTrimRange(long offset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        long end;
        try
        {
            end = checked(offset + length);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentOutOfRangeException(nameof(length), exception.Message);
        }

        if (end > _root.VirtualCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The trim range exceeds virtual capacity.");
        }

        if (end > _logicalLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The trim range must lie within logical Length.");
        }
    }

    private static bool IsPresenceSet(byte[] presence, int grain) =>
        (presence[grain >> 3] & (1 << (grain & 7))) != 0;

    private static void SetPresence(byte[] presence, int grain) =>
        presence[grain >> 3] |= (byte)(1 << (grain & 7));

    private static bool AreAllLogicalGrainsPresent(byte[] presence, int logicalBlockLength)
    {
        int grainCount = (logicalBlockLength + DifferencingFormat.SectorSize - 1) / DifferencingFormat.SectorSize;
        for (int grain = 0; grain < grainCount; grain++)
        {
            if (!IsPresenceSet(presence, grain))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateDependentId(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A dependent stream identifier cannot be empty.", nameof(id));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void ThrowIfReadOnly()
    {
        if (_readOnly)
        {
            throw new NotSupportedException("The differencing stream is read-only.");
        }
    }

    private static IOException Corruption(string message) =>
        new IOException($"The differencing stream is corrupt: {message}");

    private static IOException Corruption(string message, Exception innerException) =>
        new IOException($"The differencing stream is corrupt: {message}", innerException);

    private readonly record struct SnapshotTails(long StateTailOffset, long RegistryTailOffset);

    private sealed class BlockState(
        DifferenceBlockState state,
        long payloadOffset,
        byte[] presence)
    {
        internal DifferenceBlockState State { get; set; } = state;
        internal long PayloadOffset { get; set; } = payloadOffset;
        internal byte[] Presence { get; } = presence;
    }
}

#pragma warning restore RS0026
