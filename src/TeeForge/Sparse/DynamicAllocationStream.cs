using System.Buffers;
using System.Buffers.Binary;
using TeeForge.RandomAccess;
using TeeForge.RandomAccess.Internal;
using TeeForge.Sparse.Internal;

namespace TeeForge.Sparse;

/// <summary>Provides a sparse, block-addressed logical stream over a seekable backing stream.</summary>
public class DynamicAllocationStream : Stream, ITeeRandomAccessStream, ITeeRangeReadSource,
    IVirtualDiskStream, IDependentStreamRegistry
{
    private readonly Stream _underlying;
    private readonly ITeeRandomAccessStream? _underlyingRandomAccess;
    private readonly DynamicAllocationStreamOptions _options;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Dictionary<long, long> _batRegions = [];
    private readonly Dictionary<long, long> _trimRegions = [];
    private readonly SortedDictionary<long, long> _dependentRegions = [];
    private readonly HashSet<Guid> _dependentStreamIds = [];
    private readonly Dictionary<long, long> _pendingPatches = [];
    private readonly Dictionary<long, long> _recoveryOverlay = [];
    private readonly Dictionary<long, long> _batCache = [];
    private readonly Dictionary<long, ulong> _trimWordCache = [];
    private readonly List<RegionPageLocation> _regionPages = [];
    private readonly PriorityQueue<long, long> _freeBlocks = new();
    private readonly HashSet<long> _knownFreeBlocks = [];
    private readonly HashSet<long> _allocatedSinceScan = [];
    private readonly CancellationTokenSource _backgroundCancellation = new();
    private readonly SemaphoreSlim _freeScanSignal = new(0, 1);

    private RootState _root = null!;
    private Task? _backgroundScan;
    private Exception? _backgroundFault;
    private long _position;
    private long _logicalLength;
    private long _nextAppendOffset;
    private long _lastExhaustiveScanEnd;
    private ulong _nextJournalSequence = 1;
    private bool _disposed;
    private bool _readOnly;
    private bool _metadataMayChangeLength;
    private bool _dataWriteIdAdvanced;

    private DynamicAllocationStream(Stream underlying, DynamicAllocationStreamOptions options)
    {
        _underlying = underlying;
        _options = options;
        TeeRandomAccess.TryGet(underlying, out _underlyingRandomAccess);
    }

    /// <summary>Creates a new dynamic allocation stream over an empty backing stream.</summary>
    /// <param name="underlying">The readable, writable, seekable empty stream that receives the format.</param>
    /// <param name="virtualCapacity">The positive 4 KiB-aligned immutable logical capacity.</param>
    /// <param name="blockSize">The power-of-two allocation block size.</param>
    /// <param name="options">Creation and lifetime options.</param>
    /// <returns>The initialized stream.</returns>
    public static DynamicAllocationStream Create(
        Stream underlying,
        long virtualCapacity,
        int blockSize = DynamicAllocationFormat.DefaultBlockSize,
        DynamicAllocationStreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(underlying);
        options ??= DynamicAllocationStreamOptions.Default;
        var stream = new DynamicAllocationStream(underlying, options);
        stream.InitializeCreate(virtualCapacity, blockSize);
        return stream;
    }

    /// <summary>Creates a new dynamic allocation stream over an empty backing stream.</summary>
    public static ValueTask<DynamicAllocationStream> CreateAsync(
        Stream underlying,
        long virtualCapacity,
        int blockSize = DynamicAllocationFormat.DefaultBlockSize,
        DynamicAllocationStreamOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Create(underlying, virtualCapacity, blockSize, options));
    }

    /// <summary>Opens an existing dynamic allocation stream.</summary>
    /// <param name="underlying">The readable, seekable formatted stream.</param>
    /// <param name="options">Open and lifetime options.</param>
    /// <returns>The recovered stream.</returns>
    public static DynamicAllocationStream Open(
        Stream underlying,
        DynamicAllocationStreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(underlying);
        options ??= DynamicAllocationStreamOptions.Default;
        var stream = new DynamicAllocationStream(underlying, options);
        stream.InitializeOpen();
        return stream;
    }

    /// <summary>Opens an existing dynamic allocation stream.</summary>
    public static ValueTask<DynamicAllocationStream> OpenAsync(
        Stream underlying,
        DynamicAllocationStreamOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Open(underlying, options));
    }

    /// <summary>Gets the persistent stream identifier.</summary>
    public Guid Id => _root.Id;

    /// <summary>Gets the current caller-visible data generation.</summary>
    public Guid DataWriteId => _root.DataWriteId;

    /// <summary>Gets the physical allocation block size.</summary>
    public int BlockSize => _root.BlockSize;

    /// <summary>Gets the immutable logical capacity.</summary>
    public long VirtualCapacity => _root.VirtualCapacity;

    /// <inheritdoc />
    public bool HasDependentStreams
    {
        get
        {
            ThrowIfDisposed();
            _operationGate.Wait();
            try
            {
                return _dependentStreamIds.Count != 0;
            }
            finally
            {
                _operationGate.Release();
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<Guid> DependentStreamIds
    {
        get
        {
            ThrowIfDisposed();
            _operationGate.Wait();
            try
            {
                return _dependentStreamIds.Order().ToArray();
            }
            finally
            {
                _operationGate.Release();
            }
        }
    }

    /// <summary>Gets whether the wrapper is read-only.</summary>
    public bool IsReadOnly => _readOnly;

    /// <summary>Gets the backing stream.</summary>
    public Stream UnderlyingStream => _underlying;

    /// <inheritdoc />
    public override bool CanRead => !_disposed && _underlying.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => !_disposed && _underlying.CanSeek;

    /// <inheritdoc />
    public override bool CanWrite => !_disposed && !_readOnly && _underlying.CanWrite;

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
            ThrowBackgroundFault();
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
        ThrowBackgroundFault();
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
    public override int ReadByte()
    {
        Span<byte> value = stackalloc byte[1];
        return Read(value) == 0 ? -1 : value[0];
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowBackgroundFault();
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
        ThrowBackgroundFault();
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
        ThrowBackgroundFault();
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
        ThrowBackgroundFault();
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
    public override void WriteByte(byte value)
    {
        ReadOnlySpan<byte> data = new(in value);
        Write(data);
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        ThrowBackgroundFault();
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
        ThrowBackgroundFault();
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
        ThrowBackgroundFault();
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
        ThrowBackgroundFault();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        cancellationToken.ThrowIfCancellationRequested();
        long logicalLength = Volatile.Read(ref _logicalLength);
        long boundedLength = offset >= logicalLength ? 0 : Math.Min(length, logicalLength - offset);
        return ValueTask.FromResult<Stream>(
            new BoundedRandomAccessReadStream(this, offset, boundedLength));
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        ThrowBackgroundFault();
        _operationGate.Wait();
        try
        {
            long position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(_logicalLength + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };

            ArgumentOutOfRangeException.ThrowIfNegative(position);
            _position = position;
            return position;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public override void SetLength(long value) =>
        throw new NotSupportedException("Dynamic logical length is derived from live blocks.");

    /// <inheritdoc />
    public override void Flush()
    {
        ThrowIfDisposed();
        if (_readOnly)
        {
            return;
        }

        ThrowBackgroundFault();
        _operationGate.Wait();
        try
        {
            CommitPendingMetadata();
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
        if (_readOnly)
        {
            return;
        }

        ThrowBackgroundFault();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CommitPendingMetadataAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Discards a logical byte range without changing Position.</summary>
    public void Trim(long offset, long length)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        ValidateTrimRange(offset, length);
        if (length == 0)
        {
            return;
        }

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

    /// <summary>Discards a logical byte range without changing Position.</summary>
    public async ValueTask TrimAsync(long offset, long length, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        ValidateTrimRange(offset, length);
        if (length == 0)
        {
            return;
        }

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
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A dependent stream identifier cannot be empty.", nameof(id));
        }

        _operationGate.Wait();
        try
        {
            ThrowBackgroundFault();
            if (_dependentStreamIds.Add(id))
            {
                RewriteDependentRegistry();
                CommitPendingMetadata();
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
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A dependent stream identifier cannot be empty.", nameof(id));
        }

        _operationGate.Wait();
        try
        {
            ThrowBackgroundFault();
            if (_dependentStreamIds.Remove(id))
            {
                RewriteDependentRegistry();
                CommitPendingMetadata();
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

    /// <summary>Estimates bytes removable by metadata-only packing and trim reclamation.</summary>
    public long EstimateCompactionSavings()
    {
        ThrowIfDisposed();
        ThrowBackgroundFault();
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

    /// <summary>Estimates bytes removable by metadata-only packing and trim reclamation.</summary>
    public async ValueTask<long> EstimateCompactionSavingsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowBackgroundFault();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return EstimateCompactionSavingsCore();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Reclaims and packs physical blocks, returning resulting physical length.</summary>
    public long Compact(DynamicAllocationCompactionMode mode = DynamicAllocationCompactionMode.Fast)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        ValidateCompactionMode(mode);
        ThrowBackgroundFault();
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

    /// <summary>Reclaims and packs physical blocks, returning resulting physical length.</summary>
    public async ValueTask<long> CompactAsync(
        DynamicAllocationCompactionMode mode = DynamicAllocationCompactionMode.Fast,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        ValidateCompactionMode(mode);
        ThrowBackgroundFault();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return CompactCore(mode);
        }
        finally
        {
            _operationGate.Release();
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

        _backgroundCancellation.Cancel();
        try
        {
            _backgroundScan?.GetAwaiter().GetResult();
            if (!_readOnly)
            {
                Flush();
            }
        }
        finally
        {
            _disposed = true;
            _backgroundCancellation.Dispose();
            _freeScanSignal.Dispose();
            _operationGate.Dispose();
            if (!_options.LeaveOpen)
            {
                _underlying.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            await base.DisposeAsync().ConfigureAwait(false);
            return;
        }

        _backgroundCancellation.Cancel();
        if (_backgroundScan is not null)
        {
            await _backgroundScan.ConfigureAwait(false);
        }

        if (!_readOnly)
        {
            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }

        _disposed = true;
        _backgroundCancellation.Dispose();
        _freeScanSignal.Dispose();
        _operationGate.Dispose();
        if (!_options.LeaveOpen)
        {
            await _underlying.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void InitializeCreate(long virtualCapacity, int blockSize)
    {
        if (!DynamicAllocationFormat.IsValidBlockSize(blockSize))
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize), "Block size must be a power of two from 64 KiB through 256 MiB.");
        }

        if (!DynamicAllocationFormat.IsValidVirtualCapacity(virtualCapacity))
        {
            throw new ArgumentOutOfRangeException(
                nameof(virtualCapacity),
                "Virtual capacity must be positive and aligned to 4 KiB.");
        }

        if (!_underlying.CanRead || !_underlying.CanWrite || !_underlying.CanSeek)
        {
            throw new ArgumentException("Creation requires a readable, writable, seekable backing stream.");
        }

        if (_underlying.Length != 0)
        {
            throw new ArgumentException("Creation requires an empty backing stream.");
        }

        if (_options.ReadOnly)
        {
            throw new ArgumentException("A newly created stream cannot be forced read-only.");
        }

        Guid id = Guid.NewGuid();
        Guid dataWriteId = Guid.NewGuid();
        int journalOffset = DynamicAllocationFormat.GetJournalOffset(blockSize);
        int journalLength = DynamicAllocationFormat.GetJournalLength(blockSize);
        _root = new RootState(
            2,
            id,
            DynamicAllocationFormat.MajorVersion,
            DynamicAllocationFormat.MinorVersion,
            blockSize,
            0,
            journalOffset,
            journalLength,
            Guid.Empty,
            0,
            0,
            0,
            0,
            0,
            dataWriteId,
            virtualCapacity);

        byte[] sector = GC.AllocateUninitializedArray<byte>(DynamicAllocationFormat.SectorSize);
        DynamicAllocationFormat.WriteIdentifier(sector, id, dataWriteId, blockSize, virtualCapacity);
        WriteAt(DynamicAllocationFormat.IdentifierOffset, sector);

        RootState rootA = _root with { Generation = 1 };
        DynamicAllocationFormat.WriteRoot(sector, rootA);
        WriteAt(DynamicAllocationFormat.RootAOffset, sector);
        DynamicAllocationFormat.WriteRoot(sector, _root);
        WriteAt(DynamicAllocationFormat.RootBOffset, sector);

        int primaryLength = journalOffset - DynamicAllocationFormat.PrimaryRegionOffset;
        var primary = new RegionPage(0, DynamicAllocationFormat.GetPrimaryRegionCapacity(blockSize), [], 0);
        WriteNewRegionPage(DynamicAllocationFormat.PrimaryRegionOffset, primaryLength, primary);
        _regionPages.Add(new(primary, DynamicAllocationFormat.PrimaryRegionOffset, primaryLength));

        Array.Clear(sector);
        WriteAt(blockSize - sector.Length, sector);
        PhysicalBarrier();

        _logicalLength = 0;
        _nextAppendOffset = blockSize;
        _readOnly = false;
        _dataWriteIdAdvanced = false;
    }

    private void InitializeOpen()
    {
        if (!_underlying.CanRead || !_underlying.CanSeek)
        {
            throw new ArgumentException("Opening requires a readable, seekable backing stream.", nameof(_underlying));
        }

        if (_underlying.Length < DynamicAllocationFormat.PrimaryRegionOffset)
        {
            throw Corruption("The backing stream is too short to contain the headers.", 0);
        }

        byte[] identifierBuffer = new byte[DynamicAllocationFormat.SectorSize];
        byte[] rootABuffer = new byte[DynamicAllocationFormat.SectorSize];
        byte[] rootBBuffer = new byte[DynamicAllocationFormat.SectorSize];
        ReadExactlyAt(DynamicAllocationFormat.IdentifierOffset, identifierBuffer);
        ReadExactlyAt(DynamicAllocationFormat.RootAOffset, rootABuffer);
        ReadExactlyAt(DynamicAllocationFormat.RootBOffset, rootBBuffer);

        bool hasIdentifier = DynamicAllocationFormat.TryReadIdentifier(identifierBuffer, out FormatIdentity identity);
        bool hasA = DynamicAllocationFormat.TryReadRoot(rootABuffer, out RootState? rootA);
        bool hasB = DynamicAllocationFormat.TryReadRoot(rootBBuffer, out RootState? rootB);
        if (!hasA && !hasB)
        {
            throw Corruption("Neither redundant root is valid.", DynamicAllocationFormat.RootAOffset);
        }

        if (hasA && hasB && rootA!.Generation == rootB!.Generation && rootA != rootB)
        {
            throw Corruption("Redundant roots have equal generations but different contents.", DynamicAllocationFormat.RootAOffset);
        }

        _root = !hasB || (hasA && rootA!.Generation > rootB!.Generation) ? rootA! : rootB!;
        if (hasIdentifier &&
            (identity.Id != _root.Id || identity.BlockSize != _root.BlockSize ||
            identity.VirtualCapacity != _root.VirtualCapacity ||
            identity.MajorVersion != _root.MajorVersion))
        {
            throw Corruption("The file identifier and current root disagree.", 0);
        }

        if (_root.MajorVersion != DynamicAllocationFormat.MajorVersion)
        {
            throw new NotSupportedException($"Dynamic allocation format major version {_root.MajorVersion} is not supported.");
        }

        _readOnly = _options.ReadOnly || !_underlying.CanWrite || _root.MinorVersion != DynamicAllocationFormat.MinorVersion;
        if (!_readOnly && _root.MinorVersion != DynamicAllocationFormat.MinorVersion)
        {
            throw new NotSupportedException("Writable open requires exact format version 1.0.");
        }

        if (!_root.IsClean)
        {
            RecoverActiveJournal();
        }

        LoadRegionTables();
        if (!_root.IsClean)
        {
            long recoveredLength = RecomputeLogicalLength();
            _logicalLength = recoveredLength;
            if (!_readOnly)
            {
                PublishCleanRoot(recoveredLength);
            }
        }
        else
        {
            _logicalLength = _root.LogicalLength;
        }

        _dataWriteIdAdvanced = false;

        _nextAppendOffset = DynamicAllocationFormat.AlignUp(_underlying.Length, _root.BlockSize);
        if (!_readOnly && _options.FreeBlockQueueCapacity > 0)
        {
            StartBackgroundScan();
            RequestBackgroundScan();
        }
    }

    private void RecoverActiveJournal()
    {
        if (_underlying.Length < _root.RequiredPhysicalLength)
        {
            throw Corruption("The physical stream is shorter than the active journal requires.", _underlying.Length);
        }

        int slotCount = _root.JournalLength / DynamicAllocationFormat.SectorSize;
        var patches = new SortedDictionary<long, long>();
        byte[] entryBuffer = new byte[DynamicAllocationFormat.SectorSize];
        for (int i = 0; i < _root.ActiveLogEntryCount; i++)
        {
            int slot = (_root.ActiveLogStartSlot + i) % slotCount;
            long offset = _root.JournalOffset + ((long)slot * DynamicAllocationFormat.SectorSize);
            ReadExactlyAt(offset, entryBuffer);
            if (!DynamicAllocationFormat.TryReadJournalEntry(entryBuffer, out JournalEntry? entry) ||
                entry!.LogId != _root.ActiveLogId ||
                entry.Sequence != _root.ActiveLogFirstSequence + (ulong)i ||
                entry.EntryIndex != i ||
                entry.EntryCount != _root.ActiveLogEntryCount ||
                entry.RequiredPhysicalLength != _root.RequiredPhysicalLength)
            {
                throw Corruption("The active journal sequence is incomplete or invalid.", offset);
            }

            foreach (MetadataPatch patch in entry.Patches)
            {
                ValidatePatchTarget(patch.Offset, _root.RequiredPhysicalLength);
                patches[patch.Offset] = patch.Value;
            }
        }

        if (_readOnly)
        {
            foreach ((long offset, long value) in patches)
            {
                _recoveryOverlay[offset] = value;
            }
        }
        else
        {
            foreach ((long offset, long value) in patches)
            {
                WriteInt64At(offset, value);
            }

            PhysicalBarrier();
        }
    }

    private void LoadRegionTables()
    {
        _regionPages.Clear();
        _batRegions.Clear();
        _trimRegions.Clear();
        _dependentRegions.Clear();
        _dependentStreamIds.Clear();
        var physicalOwners = new HashSet<long> { 0 };
        long offset = DynamicAllocationFormat.PrimaryRegionOffset;
        long tableIndex = 0;
        while (true)
        {
            int length = tableIndex == 0
                ? checked((int)(_root.JournalOffset - DynamicAllocationFormat.PrimaryRegionOffset))
                : _root.BlockSize;
            int capacity = tableIndex == 0
                ? DynamicAllocationFormat.GetPrimaryRegionCapacity(_root.BlockSize)
                : DynamicAllocationFormat.GetSubRegionCapacity(_root.BlockSize);
            byte[] header = new byte[DynamicAllocationFormat.RegionHeaderSize];
            ReadMetadataExactly(offset, header);
            if (!DynamicAllocationFormat.TryReadRegionPageHeader(header, tableIndex, capacity, out int entryCount))
            {
                throw Corruption("A region table is invalid.", offset);
            }

            int prefixLength = DynamicAllocationFormat.RegionHeaderSize + (entryCount * DynamicAllocationFormat.RegionEntrySize);
            byte[] prefix = GC.AllocateUninitializedArray<byte>(prefixLength);
            ReadMetadataExactly(offset, prefix);
            byte[] link = new byte[DynamicAllocationFormat.RegionEntrySize];
            ReadMetadataExactly(offset + length - link.Length, link);
            if (!DynamicAllocationFormat.TryReadRegionPageParts(prefix, link, tableIndex, capacity, out RegionPage? page))
            {
                throw Corruption("A region table is invalid.", offset);
            }

            _regionPages.Add(new(page!, offset, length));
            foreach (RegionEntry entry in page!.Entries)
            {
                ValidatePhysicalBlock(entry.PhysicalOffset, requireComplete: true);
                if (!physicalOwners.Add(entry.PhysicalOffset))
                {
                    throw Corruption("Two metadata regions own the same physical block.", entry.PhysicalOffset);
                }

                IDictionary<long, long> target = entry.Kind switch
                {
                    DynamicAllocationFormat.BatRegionKind => _batRegions,
                    DynamicAllocationFormat.TrimRegionKind => _trimRegions,
                    DynamicAllocationFormat.DependentRegionKind => _dependentRegions,
                    _ => throw Corruption("The region table contains an unsupported kind.", entry.PhysicalOffset),
                };
                if (!target.TryAdd(entry.LogicalIndex, entry.PhysicalOffset))
                {
                    throw Corruption("A logical metadata region is duplicated.", entry.PhysicalOffset);
                }
            }

            if (page.NextOffset == 0)
            {
                break;
            }

            ValidatePhysicalBlock(page.NextOffset, requireComplete: true);
            if (!physicalOwners.Add(page.NextOffset))
            {
                throw Corruption("The region-table chain contains a loop or duplicate.", page.NextOffset);
            }

            offset = page.NextOffset;
            tableIndex++;
        }

        LoadDependentRegistry();
    }

    private void LoadDependentRegistry()
    {
        long expectedPageIndex = 0;
        long expectedOffset = _dependentRegions.Count == 0 ? 0 : _dependentRegions[0];
        foreach ((long pageIndex, long pageOffset) in _dependentRegions)
        {
            if (pageIndex != expectedPageIndex || pageOffset != expectedOffset)
            {
                throw Corruption("The dependent registry page chain is not consecutive.", pageOffset);
            }

            byte[] page = new byte[_root.BlockSize];
            ReadMetadataExactly(pageOffset, page);
            if (!DynamicAllocationFormat.TryReadDependentPage(page, pageIndex, out long nextOffset, out List<Guid> ids))
            {
                throw Corruption("A dependent registry page is invalid.", pageOffset);
            }

            foreach (Guid id in ids)
            {
                if (!_dependentStreamIds.Add(id))
                {
                    throw Corruption("A dependent stream identifier is duplicated.", pageOffset);
                }
            }

            expectedPageIndex++;
            expectedOffset = nextOffset;
        }

        if (expectedOffset != 0)
        {
            throw Corruption("The dependent registry chain references a missing page.", expectedOffset);
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
            int count = Math.Min(total - completed, _root.BlockSize - blockOffset);
            Span<byte> chunk = destination.Slice(completed, count);
            long physicalOffset = GetBatValue(logicalBlock);
            if (physicalOffset == 0 || IsTrimmed(logicalBlock))
            {
                chunk.Clear();
            }
            else
            {
                ValidatePayloadBlock(physicalOffset);
                ReadExactlyAt(physicalOffset + blockOffset, chunk);
            }

            completed += count;
        }

        return completed;
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
        if (_underlyingRandomAccess is { CanReadAt: true })
        {
            var pendingReads = new List<Task>();
            int scheduled = 0;
            while (scheduled < total)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long logicalPosition = offset + scheduled;
                long logicalBlock = logicalPosition / _root.BlockSize;
                int blockOffset = (int)(logicalPosition & (_root.BlockSize - 1L));
                int count = Math.Min(total - scheduled, _root.BlockSize - blockOffset);
                Memory<byte> chunk = destination.Slice(scheduled, count);
                long physicalOffset = GetBatValue(logicalBlock);
                if (physicalOffset == 0 || IsTrimmed(logicalBlock))
                {
                    chunk.Span.Clear();
                }
                else
                {
                    ValidatePayloadBlock(physicalOffset);
                    pendingReads.Add(ReadExactlyAtAsync(
                        physicalOffset + blockOffset,
                        chunk,
                        cancellationToken).AsTask());
                }

                scheduled += count;
            }

            await Task.WhenAll(pendingReads).ConfigureAwait(false);
            return total;
        }

        int completed = 0;
        while (completed < total)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long logicalPosition = offset + completed;
            long logicalBlock = logicalPosition / _root.BlockSize;
            int blockOffset = (int)(logicalPosition & (_root.BlockSize - 1L));
            int count = Math.Min(total - completed, _root.BlockSize - blockOffset);
            Memory<byte> chunk = destination.Slice(completed, count);
            long physicalOffset = GetBatValue(logicalBlock);
            if (physicalOffset == 0 || IsTrimmed(logicalBlock))
            {
                chunk.Span.Clear();
            }
            else
            {
                ValidatePayloadBlock(physicalOffset);
                await ReadExactlyAtAsync(physicalOffset + blockOffset, chunk, cancellationToken).ConfigureAwait(false);
            }

            completed += count;
        }

        return completed;
    }

    private void WriteCore(ReadOnlySpan<byte> source, long offset)
    {
        if (source.IsEmpty)
        {
            return;
        }

        ValidateWriteEnd(offset, source.Length);
        EnsureDataWriteId();
        int completed = 0;
        while (completed < source.Length)
        {
            long logicalPosition = offset + completed;
            long logicalBlock = logicalPosition / _root.BlockSize;
            int blockOffset = (int)(logicalPosition & (_root.BlockSize - 1L));
            int blockLogicalLength = GetLogicalBlockLength(logicalBlock);
            int count = Math.Min(source.Length - completed, blockLogicalLength - blockOffset);
            bool wholeBlock = blockOffset == 0 && count == blockLogicalLength;
            bool trimmed = IsTrimmed(logicalBlock);
            long physicalOffset = GetBatValue(logicalBlock);

            if (physicalOffset == 0)
            {
                physicalOffset = AllocatePhysicalBlock(logicalBlock);
                if (wholeBlock && blockLogicalLength == _root.BlockSize)
                {
                    WriteAt(physicalOffset, source.Slice(completed, count));
                }
                else
                {
                    ZeroPhysicalBlock(physicalOffset);
                    WriteAt(physicalOffset + blockOffset, source.Slice(completed, count));
                }

                SetBatValue(logicalBlock, physicalOffset);
            }
            else if (trimmed)
            {
                if (!wholeBlock)
                {
                    ZeroPhysicalBlock(physicalOffset);
                }

                WriteAt(physicalOffset + blockOffset, source.Slice(completed, count));
                SetTrimmed(logicalBlock, false);
            }
            else
            {
                ValidatePayloadBlock(physicalOffset);
                WriteAt(physicalOffset + blockOffset, source.Slice(completed, count));
            }

            long newLength = GetLogicalBlockEnd(logicalBlock);
            if (newLength > _logicalLength)
            {
                _logicalLength = newLength;
            }

            completed += count;
            CommitIfJournalCapacityRequires();
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

        ValidateWriteEnd(offset, source.Length);
        await EnsureDataWriteIdAsync(cancellationToken).ConfigureAwait(false);
        int completed = 0;
        while (completed < source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long logicalPosition = offset + completed;
            long logicalBlock = logicalPosition / _root.BlockSize;
            int blockOffset = (int)(logicalPosition & (_root.BlockSize - 1L));
            int blockLogicalLength = GetLogicalBlockLength(logicalBlock);
            int count = Math.Min(source.Length - completed, blockLogicalLength - blockOffset);
            bool wholeBlock = blockOffset == 0 && count == blockLogicalLength;
            bool trimmed = IsTrimmed(logicalBlock);
            long physicalOffset = GetBatValue(logicalBlock);

            if (physicalOffset == 0)
            {
                physicalOffset = AllocatePhysicalBlock(logicalBlock);
                if (wholeBlock && blockLogicalLength == _root.BlockSize)
                {
                    await WriteAtAsync(physicalOffset, source.Slice(completed, count), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await ZeroPhysicalBlockAsync(physicalOffset, cancellationToken).ConfigureAwait(false);
                    await WriteAtAsync(physicalOffset + blockOffset, source.Slice(completed, count), cancellationToken).ConfigureAwait(false);
                }

                SetBatValue(logicalBlock, physicalOffset);
            }
            else if (trimmed)
            {
                if (!wholeBlock)
                {
                    await ZeroPhysicalBlockAsync(physicalOffset, cancellationToken).ConfigureAwait(false);
                }

                await WriteAtAsync(physicalOffset + blockOffset, source.Slice(completed, count), cancellationToken).ConfigureAwait(false);
                SetTrimmed(logicalBlock, false);
            }
            else
            {
                ValidatePayloadBlock(physicalOffset);
                await WriteAtAsync(physicalOffset + blockOffset, source.Slice(completed, count), cancellationToken).ConfigureAwait(false);
            }

            long newLength = GetLogicalBlockEnd(logicalBlock);
            if (newLength > _logicalLength)
            {
                _logicalLength = newLength;
            }

            completed += count;
            if (PendingEntryCount > JournalPatchCapacity)
            {
                await CommitPendingMetadataAsync(cancellationToken).ConfigureAwait(false);
            }
        }

    }

    private void TrimCore(long offset, long length)
    {
        if (length != 0)
        {
            EnsureDataWriteId();
        }

        long end = offset + length;
        long cursor = offset;
        while (cursor < end)
        {
            long logicalBlock = cursor / _root.BlockSize;
            int blockOffset = (int)(cursor & (_root.BlockSize - 1L));
            int blockLength = GetLogicalBlockLength(logicalBlock);
            int count = (int)Math.Min(end - cursor, blockLength - blockOffset);
            long physicalOffset = GetBatValue(logicalBlock);
            if (physicalOffset != 0)
            {
                if (blockOffset == 0 && count == blockLength)
                {
                    SetTrimmed(logicalBlock, true);
                    if (GetLogicalBlockEnd(logicalBlock) == _logicalLength)
                    {
                        _metadataMayChangeLength = true;
                    }
                }
                else if (!IsTrimmed(logicalBlock))
                {
                    ZeroRange(physicalOffset + blockOffset, count);
                }
            }

            cursor += count;
            CommitIfJournalCapacityRequires();
        }

        if (_metadataMayChangeLength)
        {
            _logicalLength = RecomputeLogicalLength();
            _metadataMayChangeLength = false;
        }
    }

    private async ValueTask TrimCoreAsync(long offset, long length, CancellationToken cancellationToken)
    {
        if (length != 0)
        {
            await EnsureDataWriteIdAsync(cancellationToken).ConfigureAwait(false);
        }

        long end = offset + length;
        long cursor = offset;
        while (cursor < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long logicalBlock = cursor / _root.BlockSize;
            int blockOffset = (int)(cursor & (_root.BlockSize - 1L));
            int blockLength = GetLogicalBlockLength(logicalBlock);
            int count = (int)Math.Min(end - cursor, blockLength - blockOffset);
            long physicalOffset = GetBatValue(logicalBlock);
            if (physicalOffset != 0)
            {
                if (blockOffset == 0 && count == blockLength)
                {
                    SetTrimmed(logicalBlock, true);
                    if (GetLogicalBlockEnd(logicalBlock) == _logicalLength)
                    {
                        _metadataMayChangeLength = true;
                    }
                }
                else if (!IsTrimmed(logicalBlock))
                {
                    await ZeroRangeAsync(physicalOffset + blockOffset, count, cancellationToken).ConfigureAwait(false);
                }
            }

            cursor += count;
            if (PendingEntryCount > JournalPatchCapacity)
            {
                await CommitPendingMetadataAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (_metadataMayChangeLength)
        {
            _logicalLength = RecomputeLogicalLength();
            _metadataMayChangeLength = false;
        }
    }

    private long GetBatValue(long logicalBlock)
    {
        if (_batCache.TryGetValue(logicalBlock, out long cached))
        {
            return cached;
        }

        long regionIndex = logicalBlock / EntriesPerBatRegion;
        if (!_batRegions.TryGetValue(regionIndex, out long regionOffset))
        {
            return 0;
        }

        long localIndex = logicalBlock % EntriesPerBatRegion;
        long targetOffset = checked(regionOffset + (localIndex * 8));
        long value = ReadMetadataInt64(targetOffset);
        if (value != 0)
        {
            ValidatePayloadBlock(value);
        }

        _batCache[logicalBlock] = value;
        return value;
    }

    private void SetBatValue(long logicalBlock, long value)
    {
        long regionIndex = logicalBlock / EntriesPerBatRegion;
        long regionOffset = EnsureRegion(DynamicAllocationFormat.BatRegionKind, regionIndex);
        long localIndex = logicalBlock % EntriesPerBatRegion;
        long targetOffset = checked(regionOffset + (localIndex * 8));
        QueuePatch(targetOffset, value);
        _batCache[logicalBlock] = value;
        if (value != 0)
        {
            _allocatedSinceScan.Add(value);
        }
    }

    private bool IsTrimmed(long logicalBlock)
    {
        long wordIndex = logicalBlock >> 6;
        if (!_trimWordCache.TryGetValue(wordIndex, out ulong word))
        {
            long regionIndex = logicalBlock / BitsPerTrimRegion;
            if (!_trimRegions.TryGetValue(regionIndex, out long regionOffset))
            {
                return false;
            }

            long bitWithinRegion = logicalBlock % BitsPerTrimRegion;
            long wordWithinRegion = bitWithinRegion >> 6;
            word = unchecked((ulong)ReadMetadataInt64(regionOffset + (wordWithinRegion * 8)));
            _trimWordCache[wordIndex] = word;
        }

        return (word & (1UL << (int)(logicalBlock & 63))) != 0;
    }

    private void SetTrimmed(long logicalBlock, bool trimmed)
    {
        long wordIndex = logicalBlock >> 6;
        ulong word;
        long regionIndex = logicalBlock / BitsPerTrimRegion;
        long regionOffset;
        if (!_trimRegions.TryGetValue(regionIndex, out regionOffset))
        {
            if (!trimmed)
            {
                return;
            }

            regionOffset = EnsureRegion(DynamicAllocationFormat.TrimRegionKind, regionIndex);
            word = 0;
        }
        else if (!_trimWordCache.TryGetValue(wordIndex, out word))
        {
            long bitWithinRegion = logicalBlock % BitsPerTrimRegion;
            word = unchecked((ulong)ReadMetadataInt64(regionOffset + ((bitWithinRegion >> 6) * 8)));
        }

        ulong mask = 1UL << (int)(logicalBlock & 63);
        ulong updated = trimmed ? word | mask : word & ~mask;
        if (updated == word)
        {
            return;
        }

        long withinRegion = (logicalBlock % BitsPerTrimRegion) >> 6;
        QueuePatch(regionOffset + (withinRegion * 8), unchecked((long)updated));
        _trimWordCache[wordIndex] = updated;
    }

    private long EnsureRegion(uint kind, long logicalRegionIndex)
    {
        Dictionary<long, long> map = kind == DynamicAllocationFormat.BatRegionKind ? _batRegions : _trimRegions;
        if (map.TryGetValue(logicalRegionIndex, out long existing))
        {
            return existing;
        }

        long physicalOffset = AllocatePhysicalBlock(logicalBlock: null);
        ZeroPhysicalBlock(physicalOffset);
        RegionPageLocation pageLocation = _regionPages.FirstOrDefault(static p => p.Page.Entries.Count < p.Page.Capacity)
            ?? AddSubRegionPage();
        pageLocation.Page.Entries.Add(new RegionEntry(kind, DynamicAllocationFormat.RequiredRegionFlag, logicalRegionIndex, physicalOffset));
        QueueRegionPage(pageLocation);
        map.Add(logicalRegionIndex, physicalOffset);
        return physicalOffset;
    }

    private RegionPageLocation AddSubRegionPage()
    {
        RegionPageLocation parent = _regionPages[^1];
        long offset = AllocatePhysicalBlock(logicalBlock: null);
        var page = new RegionPage(parent.Page.TableIndex + 1, DynamicAllocationFormat.GetSubRegionCapacity(_root.BlockSize), [], 0);
        WriteNewRegionPage(offset, _root.BlockSize, page);
        parent.Page.NextOffset = offset;
        QueueRegionPage(parent);
        var location = new RegionPageLocation(page, offset, _root.BlockSize);
        _regionPages.Add(location);
        return location;
    }

    private void QueueRegionPage(RegionPageLocation location)
    {
        int prefixLength = DynamicAllocationFormat.RegionHeaderSize +
            (location.Page.Entries.Count * DynamicAllocationFormat.RegionEntrySize);
        byte[] prefix = GC.AllocateUninitializedArray<byte>(prefixLength);
        byte[] link = new byte[DynamicAllocationFormat.RegionEntrySize];
        DynamicAllocationFormat.WriteRegionPageParts(prefix, link, location.Page);
        QueueWordRange(location.Offset, prefix);
        QueueWordRange(location.Offset + location.Length - link.Length, link);
    }

    private void QueueWordRange(long offset, ReadOnlySpan<byte> data)
    {
        if ((data.Length & 7) != 0)
        {
            throw new InvalidOperationException("Metadata patch ranges must be 8-byte aligned.");
        }

        for (int i = 0; i < data.Length; i += 8)
        {
            QueuePatch(offset + i, BinaryPrimitives.ReadInt64LittleEndian(data[i..]));
        }
    }

    private void RewriteDependentRegistry()
    {
        int pageCapacity = DynamicAllocationFormat.GetDependentPageCapacity(_root.BlockSize);
        int requiredPages = Math.Max(1, (_dependentStreamIds.Count + pageCapacity - 1) / pageCapacity);
        while (_dependentRegions.Count < requiredPages)
        {
            AddDependentPage(_dependentRegions.Count);
        }

        Guid[] ordered = _dependentStreamIds.Order().ToArray();
        for (int pageIndex = 0; pageIndex < _dependentRegions.Count; pageIndex++)
        {
            long pageOffset = _dependentRegions[pageIndex];
            long nextOffset = pageIndex + 1 < _dependentRegions.Count
                ? _dependentRegions[pageIndex + 1]
                : 0;
            Guid[] pageIds = ordered
                .Skip(pageIndex * pageCapacity)
                .Take(pageCapacity)
                .ToArray();
            byte[] updated = new byte[_root.BlockSize];
            DynamicAllocationFormat.WriteDependentPage(updated, pageIndex, nextOffset, pageIds);
            byte[] current = new byte[_root.BlockSize];
            ReadMetadataExactly(pageOffset, current);
            for (int cursor = 0; cursor < updated.Length; cursor += 8)
            {
                long value = BinaryPrimitives.ReadInt64LittleEndian(updated.AsSpan(cursor, 8));
                if (value != BinaryPrimitives.ReadInt64LittleEndian(current.AsSpan(cursor, 8)))
                {
                    QueuePatch(pageOffset + cursor, value);
                }
            }
        }
    }

    private void AddDependentPage(long pageIndex)
    {
        long physicalOffset = AllocatePhysicalBlock(logicalBlock: null);
        byte[] pageBuffer = new byte[_root.BlockSize];
        DynamicAllocationFormat.WriteDependentPage(pageBuffer, pageIndex, 0, []);
        WriteAt(physicalOffset, pageBuffer);
        PhysicalBarrier();

        RegionPageLocation tablePage = _regionPages.FirstOrDefault(static page => page.Page.Entries.Count < page.Page.Capacity)
            ?? AddSubRegionPage();
        tablePage.Page.Entries.Add(new RegionEntry(
            DynamicAllocationFormat.DependentRegionKind,
            DynamicAllocationFormat.RequiredRegionFlag,
            pageIndex,
            physicalOffset));
        QueueRegionPage(tablePage);
        _dependentRegions.Add(pageIndex, physicalOffset);
    }

    private void RewriteDependentPageLink(long pageIndex, long nextOffset)
    {
        long pageOffset = _dependentRegions[pageIndex];
        byte[] current = new byte[_root.BlockSize];
        ReadMetadataExactly(pageOffset, current);
        if (!DynamicAllocationFormat.TryReadDependentPage(current, pageIndex, out _, out List<Guid> ids))
        {
            throw Corruption("A dependent registry page is invalid.", pageOffset);
        }

        byte[] updated = new byte[_root.BlockSize];
        DynamicAllocationFormat.WriteDependentPage(updated, pageIndex, nextOffset, ids);
        for (int cursor = 0; cursor < updated.Length; cursor += 8)
        {
            long value = BinaryPrimitives.ReadInt64LittleEndian(updated.AsSpan(cursor, 8));
            if (value != BinaryPrimitives.ReadInt64LittleEndian(current.AsSpan(cursor, 8)))
            {
                QueuePatch(pageOffset + cursor, value);
            }
        }
    }

    private void QueuePatch(long offset, long value)
    {
        ValidatePatchTarget(offset, Math.Max(_nextAppendOffset, _underlying.Length));
        _pendingPatches[offset] = value;
    }

    private long ReadMetadataInt64(long offset)
    {
        if (_pendingPatches.TryGetValue(offset, out long pending))
        {
            return pending;
        }

        if (_recoveryOverlay.TryGetValue(offset, out long recovered))
        {
            return recovered;
        }

        Span<byte> bytes = stackalloc byte[8];
        ReadExactlyAt(offset, bytes);
        return BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }

    private void ReadMetadataExactly(long offset, Span<byte> destination)
    {
        ReadExactlyAt(offset, destination);
        ApplyWordOverlay(offset, destination, _recoveryOverlay);
        ApplyWordOverlay(offset, destination, _pendingPatches);
    }

    private static void ApplyWordOverlay(long offset, Span<byte> destination, Dictionary<long, long> overlay)
    {
        long end = checked(offset + destination.Length);
        foreach ((long patchOffset, long value) in overlay)
        {
            if (patchOffset >= offset && patchOffset + 8 <= end)
            {
                BinaryPrimitives.WriteInt64LittleEndian(destination[(int)(patchOffset - offset)..], value);
            }
        }
    }

    private int JournalPatchCapacity =>
        (_root.JournalLength / DynamicAllocationFormat.SectorSize) * DynamicAllocationFormat.JournalPatchesPerEntry;

    private int PendingEntryCount =>
        (_pendingPatches.Count + DynamicAllocationFormat.JournalPatchesPerEntry - 1) /
        DynamicAllocationFormat.JournalPatchesPerEntry;

    private long EntriesPerBatRegion => _root.BlockSize / 8L;

    private long BitsPerTrimRegion => (long)_root.BlockSize * 8L;

    private void CommitIfJournalCapacityRequires()
    {
        if (_pendingPatches.Count > JournalPatchCapacity)
        {
            CommitPendingMetadata();
        }
    }

    private void CommitPendingMetadata()
    {
        if (_pendingPatches.Count == 0)
        {
            PhysicalBarrier();
            return;
        }

        while (_pendingPatches.Count > 0)
        {
            MetadataPatch[] batch = _pendingPatches
                .OrderBy(static item => item.Key)
                .Take(JournalPatchCapacity)
                .Select(static item => new MetadataPatch(item.Key, item.Value))
                .ToArray();
            CommitBatch(batch);
            foreach (MetadataPatch patch in batch)
            {
                _pendingPatches.Remove(patch.Offset);
            }
        }
    }

    private async ValueTask CommitPendingMetadataAsync(CancellationToken cancellationToken)
    {
        if (_pendingPatches.Count == 0)
        {
            await PhysicalBarrierAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        while (_pendingPatches.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MetadataPatch[] batch = _pendingPatches
                .OrderBy(static item => item.Key)
                .Take(JournalPatchCapacity)
                .Select(static item => new MetadataPatch(item.Key, item.Value))
                .ToArray();
            await CommitBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            foreach (MetadataPatch patch in batch)
            {
                _pendingPatches.Remove(patch.Offset);
            }
        }
    }

    private void CommitBatch(ReadOnlySpan<MetadataPatch> patches)
    {
        int entryCount = (patches.Length + DynamicAllocationFormat.JournalPatchesPerEntry - 1) /
            DynamicAllocationFormat.JournalPatchesPerEntry;
        int slotCount = _root.JournalLength / DynamicAllocationFormat.SectorSize;
        Guid logId = Guid.NewGuid();
        ulong firstSequence = _nextJournalSequence;
        long requiredLength = Math.Max(_underlying.Length, _nextAppendOffset);

        PhysicalBarrier();
        byte[] entryBuffer = new byte[DynamicAllocationFormat.SectorSize];
        for (int i = 0; i < entryCount; i++)
        {
            int patchStart = i * DynamicAllocationFormat.JournalPatchesPerEntry;
            int patchCount = Math.Min(DynamicAllocationFormat.JournalPatchesPerEntry, patches.Length - patchStart);
            DynamicAllocationFormat.WriteJournalEntry(
                entryBuffer,
                logId,
                firstSequence + (ulong)i,
                i,
                entryCount,
                requiredLength,
                patches.Slice(patchStart, patchCount));
            int slot = (_root.NextJournalSlot + i) % slotCount;
            WriteAt(_root.JournalOffset + ((long)slot * DynamicAllocationFormat.SectorSize), entryBuffer);
        }

        PhysicalBarrier();
        RootState active = _root with
        {
            Generation = checked(_root.Generation + 1),
            ActiveLogId = logId,
            ActiveLogStartSlot = _root.NextJournalSlot,
            ActiveLogEntryCount = entryCount,
            ActiveLogFirstSequence = firstSequence,
            RequiredPhysicalLength = requiredLength,
        };
        PublishRoot(active);

        foreach (MetadataPatch patch in patches)
        {
            WriteInt64At(patch.Offset, patch.Value);
        }

        PhysicalBarrier();
        int nextSlot = (_root.NextJournalSlot + entryCount) % slotCount;
        RootState clean = active with
        {
            Generation = checked(active.Generation + 1),
            LogicalLength = _logicalLength,
            ActiveLogId = Guid.Empty,
            ActiveLogStartSlot = 0,
            ActiveLogEntryCount = 0,
            ActiveLogFirstSequence = 0,
            NextJournalSlot = nextSlot,
            RequiredPhysicalLength = 0,
        };
        PublishRoot(clean);
        _nextJournalSequence = firstSequence + (ulong)entryCount;
    }

    private async ValueTask CommitBatchAsync(MetadataPatch[] patches, CancellationToken cancellationToken)
    {
        int entryCount = (patches.Length + DynamicAllocationFormat.JournalPatchesPerEntry - 1) /
            DynamicAllocationFormat.JournalPatchesPerEntry;
        int slotCount = _root.JournalLength / DynamicAllocationFormat.SectorSize;
        Guid logId = Guid.NewGuid();
        ulong firstSequence = _nextJournalSequence;
        long requiredLength = Math.Max(_underlying.Length, _nextAppendOffset);

        await PhysicalBarrierAsync(cancellationToken).ConfigureAwait(false);
        if (_underlyingRandomAccess is { CanWriteAt: true })
        {
            Task[] journalWrites = new Task[entryCount];
            for (int i = 0; i < entryCount; i++)
            {
                byte[] entryBuffer = new byte[DynamicAllocationFormat.SectorSize];
                int patchStart = i * DynamicAllocationFormat.JournalPatchesPerEntry;
                int patchCount = Math.Min(DynamicAllocationFormat.JournalPatchesPerEntry, patches.Length - patchStart);
                DynamicAllocationFormat.WriteJournalEntry(
                    entryBuffer,
                    logId,
                    firstSequence + (ulong)i,
                    i,
                    entryCount,
                    requiredLength,
                    patches.AsSpan(patchStart, patchCount));
                int slot = (_root.NextJournalSlot + i) % slotCount;
                journalWrites[i] = WriteAtAsync(
                    _root.JournalOffset + ((long)slot * DynamicAllocationFormat.SectorSize),
                    entryBuffer,
                    cancellationToken).AsTask();
            }

            await Task.WhenAll(journalWrites).ConfigureAwait(false);
        }
        else
        {
            byte[] entryBuffer = new byte[DynamicAllocationFormat.SectorSize];
            for (int i = 0; i < entryCount; i++)
            {
                int patchStart = i * DynamicAllocationFormat.JournalPatchesPerEntry;
                int patchCount = Math.Min(DynamicAllocationFormat.JournalPatchesPerEntry, patches.Length - patchStart);
                DynamicAllocationFormat.WriteJournalEntry(
                    entryBuffer,
                    logId,
                    firstSequence + (ulong)i,
                    i,
                    entryCount,
                    requiredLength,
                    patches.AsSpan(patchStart, patchCount));
                int slot = (_root.NextJournalSlot + i) % slotCount;
                await WriteAtAsync(
                    _root.JournalOffset + ((long)slot * DynamicAllocationFormat.SectorSize),
                    entryBuffer,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await PhysicalBarrierAsync(cancellationToken).ConfigureAwait(false);
        RootState active = _root with
        {
            Generation = checked(_root.Generation + 1),
            ActiveLogId = logId,
            ActiveLogStartSlot = _root.NextJournalSlot,
            ActiveLogEntryCount = entryCount,
            ActiveLogFirstSequence = firstSequence,
            RequiredPhysicalLength = requiredLength,
        };
        await PublishRootAsync(active, cancellationToken).ConfigureAwait(false);

        if (_underlyingRandomAccess is { CanWriteAt: true })
        {
            Task[] homeWrites = new Task[patches.Length];
            for (int index = 0; index < patches.Length; index++)
            {
                MetadataPatch patch = patches[index];
                homeWrites[index] = WriteInt64AtAsync(
                    patch.Offset,
                    patch.Value,
                    cancellationToken).AsTask();
            }

            await Task.WhenAll(homeWrites).ConfigureAwait(false);
        }
        else
        {
            foreach (MetadataPatch patch in patches)
            {
                await WriteInt64AtAsync(patch.Offset, patch.Value, cancellationToken).ConfigureAwait(false);
            }
        }

        await PhysicalBarrierAsync(cancellationToken).ConfigureAwait(false);
        int nextSlot = (_root.NextJournalSlot + entryCount) % slotCount;
        RootState clean = active with
        {
            Generation = checked(active.Generation + 1),
            LogicalLength = _logicalLength,
            ActiveLogId = Guid.Empty,
            ActiveLogStartSlot = 0,
            ActiveLogEntryCount = 0,
            ActiveLogFirstSequence = 0,
            NextJournalSlot = nextSlot,
            RequiredPhysicalLength = 0,
        };
        await PublishRootAsync(clean, cancellationToken).ConfigureAwait(false);
        _nextJournalSequence = firstSequence + (ulong)entryCount;
    }

    private void PublishCleanRoot(long logicalLength)
    {
        int slotCount = _root.JournalLength / DynamicAllocationFormat.SectorSize;
        int nextSlot = (_root.ActiveLogStartSlot + _root.ActiveLogEntryCount) % slotCount;
        RootState clean = _root with
        {
            Generation = checked(_root.Generation + 1),
            LogicalLength = logicalLength,
            ActiveLogId = Guid.Empty,
            ActiveLogStartSlot = 0,
            ActiveLogEntryCount = 0,
            ActiveLogFirstSequence = 0,
            NextJournalSlot = nextSlot,
            RequiredPhysicalLength = 0,
        };
        PublishRoot(clean);
    }

    private void PublishRoot(RootState root)
    {
        byte[] buffer = new byte[DynamicAllocationFormat.SectorSize];
        DynamicAllocationFormat.WriteRoot(buffer, root);
        int offset = (root.Generation & 1UL) == 0
            ? DynamicAllocationFormat.RootBOffset
            : DynamicAllocationFormat.RootAOffset;
        WriteAt(offset, buffer);
        PhysicalBarrier();
        _root = root;
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

    private async ValueTask PublishRootAsync(RootState root, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[DynamicAllocationFormat.SectorSize];
        DynamicAllocationFormat.WriteRoot(buffer, root);
        int offset = (root.Generation & 1UL) == 0
            ? DynamicAllocationFormat.RootBOffset
            : DynamicAllocationFormat.RootAOffset;
        await WriteAtAsync(offset, buffer, cancellationToken).ConfigureAwait(false);
        await PhysicalBarrierAsync(cancellationToken).ConfigureAwait(false);
        _root = root;
    }

    private long AllocatePhysicalBlock(long? logicalBlock)
    {
        long candidate = 0;
        if (logicalBlock is > 0)
        {
            long previous = GetBatValue(logicalBlock.Value - 1);
            if (previous != 0)
            {
                long successor = checked(previous + _root.BlockSize);
                if (successor == _nextAppendOffset || _knownFreeBlocks.Remove(successor))
                {
                    candidate = successor;
                }
            }
        }

        if (candidate == 0)
        {
            while (_freeBlocks.TryDequeue(out long free, out _))
            {
                if (_knownFreeBlocks.Remove(free))
                {
                    candidate = free;
                    break;
                }
            }
        }

        if (candidate == 0)
        {
            candidate = _nextAppendOffset;
        }

        if (candidate == _nextAppendOffset)
        {
            _nextAppendOffset = checked(candidate + _root.BlockSize);
            if (_lastExhaustiveScanEnd == candidate)
            {
                _lastExhaustiveScanEnd = _nextAppendOffset;
            }
        }

        _allocatedSinceScan.Add(candidate);
        if (_knownFreeBlocks.Count <= _options.FreeBlockQueueLowWatermark)
        {
            RequestBackgroundScan();
        }

        return candidate;
    }

    private void ReleasePhysicalBlock(long offset)
    {
        if (offset <= 0 || _options.FreeBlockQueueCapacity == 0 ||
            _knownFreeBlocks.Count >= _options.FreeBlockQueueCapacity || !_knownFreeBlocks.Add(offset))
        {
            return;
        }

        _freeBlocks.Enqueue(offset, offset);
    }

    private long EstimateCompactionSavingsCore()
    {
        CommitPendingMetadata();
        IReadOnlyList<AllocatedPayload> payload = EnumeratePayloadBlocks(includeTrimmed: false);
        long metadataCount = _batRegions.Count + _trimRegions.Count + _dependentRegions.Count +
            Math.Max(0, _regionPages.Count - 1);
        long idealBlockCount = checked(1 + metadataCount + payload.Count);
        long idealLength = checked(idealBlockCount * _root.BlockSize);
        return Math.Max(0, _underlying.Length - idealLength);
    }

    private long CompactCore(DynamicAllocationCompactionMode mode)
    {
        CommitPendingMetadata();
        ReleaseTrimmedBlocks();
        CommitPendingMetadata();
        RemoveEmptyMetadataRegions();
        CommitPendingMetadata();

        if (mode == DynamicAllocationCompactionMode.Slow)
        {
            foreach (AllocatedPayload payload in EnumeratePayloadBlocks(includeTrimmed: false))
            {
                if (IsPhysicalBlockZero(payload.PhysicalOffset))
                {
                    SetBatValue(payload.LogicalBlock, 0);
                    ReleasePhysicalBlock(payload.PhysicalOffset);
                }
            }

            _logicalLength = RecomputeLogicalLength();
            CommitPendingMetadata();
            RemoveEmptyMetadataRegions();
            CommitPendingMetadata();
        }

        PackPhysicalBlocks();
        long targetLength = GetPackedPhysicalLength();
        try
        {
            _underlying.SetLength(targetLength);
            PhysicalBarrier();
            _nextAppendOffset = targetLength;
        }
        catch (NotSupportedException)
        {
            return _underlying.Length;
        }

        return _underlying.Length;
    }

    private void RemoveEmptyMetadataRegions()
    {
        RemoveEmptyRegions(_trimRegions, DynamicAllocationFormat.TrimRegionKind);
        RemoveEmptyRegions(_batRegions, DynamicAllocationFormat.BatRegionKind);
    }

    private void RemoveEmptyRegions(Dictionary<long, long> regions, uint kind)
    {
        foreach ((long logicalIndex, long physicalOffset) in regions.ToArray())
        {
            if (!IsPhysicalBlockZero(physicalOffset))
            {
                continue;
            }

            RegionPageLocation page = _regionPages.Single(item =>
                item.Page.Entries.Any(entry => entry.Kind == kind && entry.LogicalIndex == logicalIndex));
            int entryIndex = page.Page.Entries.FindIndex(entry => entry.Kind == kind && entry.LogicalIndex == logicalIndex);
            page.Page.Entries.RemoveAt(entryIndex);
            QueueRegionPage(page);
            regions.Remove(logicalIndex);
            ReleasePhysicalBlock(physicalOffset);
        }

        if (kind == DynamicAllocationFormat.TrimRegionKind)
        {
            _trimWordCache.Clear();
        }
    }

    private void ReleaseTrimmedBlocks()
    {
        foreach (AllocatedPayload payload in EnumeratePayloadBlocks(includeTrimmed: true))
        {
            if (!payload.Trimmed)
            {
                continue;
            }

            SetBatValue(payload.LogicalBlock, 0);
            SetTrimmed(payload.LogicalBlock, false);
            ReleasePhysicalBlock(payload.PhysicalOffset);
            CommitIfJournalCapacityRequires();
        }

        _logicalLength = RecomputeLogicalLength();
    }

    private List<AllocatedPayload> EnumeratePayloadBlocks(bool includeTrimmed)
    {
        var result = new List<AllocatedPayload>();
        foreach ((long regionIndex, long regionOffset) in _batRegions.OrderBy(static item => item.Key))
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(_root.BlockSize, 64 * 1024));
            try
            {
                int entriesPerChunk = buffer.Length / 8;
                long baseLogical = checked(regionIndex * EntriesPerBatRegion);
                for (long entry = 0; entry < EntriesPerBatRegion; entry += entriesPerChunk)
                {
                    int entryCount = (int)Math.Min(entriesPerChunk, EntriesPerBatRegion - entry);
                    Span<byte> chunk = buffer.AsSpan(0, entryCount * 8);
                    ReadMetadataExactly(regionOffset + (entry * 8), chunk);
                    for (int i = 0; i < entryCount; i++)
                    {
                        long physical = BinaryPrimitives.ReadInt64LittleEndian(chunk[(i * 8)..]);
                        if (physical == 0)
                        {
                            continue;
                        }

                        ValidatePayloadBlock(physical);
                        long logical = baseLogical + entry + i;
                        bool trimmed = IsTrimmed(logical);
                        if (includeTrimmed || !trimmed)
                        {
                            result.Add(new(logical, physical, trimmed));
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return result;
    }

    private long RecomputeLogicalLength()
    {
        long highest = -1;
        foreach (AllocatedPayload payload in EnumeratePayloadBlocks(includeTrimmed: false))
        {
            if (payload.LogicalBlock > highest)
            {
                highest = payload.LogicalBlock;
            }
        }

        return highest < 0 ? 0 : GetLogicalBlockEnd(highest);
    }

    private void PackPhysicalBlocks()
    {
        var owners = new SortedDictionary<long, PhysicalOwner>();
        foreach (AllocatedPayload payload in EnumeratePayloadBlocks(includeTrimmed: false))
        {
            if (!owners.TryAdd(payload.PhysicalOffset, PhysicalOwner.ForPayload(payload.LogicalBlock)))
            {
                throw Corruption("Two logical blocks own the same physical block.", payload.PhysicalOffset);
            }
        }

        foreach ((long index, long offset) in _batRegions)
        {
            if (!owners.TryAdd(offset, PhysicalOwner.ForRegion(DynamicAllocationFormat.BatRegionKind, index)))
            {
                throw Corruption("Payload and metadata overlap.", offset);
            }
        }

        foreach ((long index, long offset) in _trimRegions)
        {
            if (!owners.TryAdd(offset, PhysicalOwner.ForRegion(DynamicAllocationFormat.TrimRegionKind, index)))
            {
                throw Corruption("Payload and metadata overlap.", offset);
            }
        }

        foreach ((long index, long offset) in _dependentRegions)
        {
            if (!owners.TryAdd(offset, PhysicalOwner.ForRegion(DynamicAllocationFormat.DependentRegionKind, index)))
            {
                throw Corruption("Dependent registry metadata overlaps another block.", offset);
            }
        }

        foreach (RegionPageLocation page in _regionPages.Skip(1))
        {
            if (!owners.TryAdd(page.Offset, PhysicalOwner.ForSubRegion(page.Page.TableIndex)))
            {
                throw Corruption("Region-table metadata overlaps another block.", page.Offset);
            }
        }

        long target = _root.BlockSize;
        while (owners.Count > 0)
        {
            if (owners.ContainsKey(target))
            {
                target += _root.BlockSize;
                continue;
            }

            KeyValuePair<long, PhysicalOwner> last = owners.Last();
            if (last.Key <= target)
            {
                break;
            }

            CopyPhysicalBlock(last.Key, target);
            PhysicalBarrier();
            UpdatePhysicalOwner(last.Value, target);
            CommitPendingMetadata();
            owners.Remove(last.Key);
            owners.Add(target, last.Value);
            ReleasePhysicalBlock(last.Key);
            target += _root.BlockSize;
        }
    }

    private void UpdatePhysicalOwner(PhysicalOwner owner, long newOffset)
    {
        if (owner.Kind == PhysicalOwnerKind.Payload)
        {
            SetBatValue(owner.LogicalIndex, newOffset);
            return;
        }

        if (owner.Kind == PhysicalOwnerKind.SubRegion)
        {
            RegionPageLocation location = _regionPages.Single(item => item.Page.TableIndex == owner.LogicalIndex);
            RegionPageLocation parent = _regionPages.Single(item => item.Page.TableIndex == owner.LogicalIndex - 1);
            parent.Page.NextOffset = newOffset;
            QueueRegionPage(parent);
            location.Offset = newOffset;
            return;
        }

        uint regionKind = owner.Kind switch
        {
            PhysicalOwnerKind.BatRegion => DynamicAllocationFormat.BatRegionKind,
            PhysicalOwnerKind.TrimRegion => DynamicAllocationFormat.TrimRegionKind,
            PhysicalOwnerKind.DependentRegion => DynamicAllocationFormat.DependentRegionKind,
            _ => throw new InvalidOperationException("Unsupported physical owner kind."),
        };
        RegionPageLocation page = _regionPages.Single(item =>
            item.Page.Entries.Any(entry => entry.Kind == regionKind && entry.LogicalIndex == owner.LogicalIndex));
        int entryIndex = page.Page.Entries.FindIndex(entry =>
            entry.Kind == regionKind && entry.LogicalIndex == owner.LogicalIndex);
        page.Page.Entries[entryIndex] = page.Page.Entries[entryIndex] with { PhysicalOffset = newOffset };
        QueueRegionPage(page);
        if (regionKind == DynamicAllocationFormat.DependentRegionKind)
        {
            _dependentRegions[owner.LogicalIndex] = newOffset;
            if (owner.LogicalIndex > 0)
            {
                RewriteDependentPageLink(owner.LogicalIndex - 1, newOffset);
            }
        }
        else
        {
            Dictionary<long, long> map = regionKind == DynamicAllocationFormat.BatRegionKind ? _batRegions : _trimRegions;
            map[owner.LogicalIndex] = newOffset;
        }
    }

    private long GetPackedPhysicalLength()
    {
        long maximum = 0;
        foreach (AllocatedPayload payload in EnumeratePayloadBlocks(includeTrimmed: false))
        {
            maximum = Math.Max(maximum, payload.PhysicalOffset);
        }

        foreach (long offset in _batRegions.Values
            .Concat(_trimRegions.Values)
            .Concat(_dependentRegions.Values)
            .Concat(_regionPages.Skip(1).Select(static p => p.Offset)))
        {
            maximum = Math.Max(maximum, offset);
        }

        return maximum == 0 ? _root.BlockSize : checked(maximum + _root.BlockSize);
    }

    private void StartBackgroundScan()
    {
        _backgroundScan = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await _freeScanSignal.WaitAsync(_backgroundCancellation.Token).ConfigureAwait(false);
                    long[] metadataOffsets;
                    long[] batRegionOffsets;
                    await _operationGate.WaitAsync(_backgroundCancellation.Token).ConfigureAwait(false);
                    try
                    {
                        if (_knownFreeBlocks.Count > _options.FreeBlockQueueLowWatermark ||
                            _lastExhaustiveScanEnd >= _nextAppendOffset)
                        {
                            continue;
                        }

                        metadataOffsets = _batRegions.Values
                            .Concat(_trimRegions.Values)
                            .Concat(_dependentRegions.Values)
                            .Concat(_regionPages.Skip(1).Select(static p => p.Offset))
                            .ToArray();
                        batRegionOffsets = _batRegions.OrderBy(static item => item.Key)
                            .Select(static item => item.Value)
                            .ToArray();
                    }
                    finally
                    {
                        _operationGate.Release();
                    }

                    var occupied = new HashSet<long> { 0 };
                    foreach (long offset in metadataOffsets)
                    {
                        occupied.Add(offset);
                    }

                    foreach (long regionOffset in batRegionOffsets)
                    {
                        _backgroundCancellation.Token.ThrowIfCancellationRequested();
                        await _operationGate.WaitAsync(_backgroundCancellation.Token).ConfigureAwait(false);
                        try
                        {
                            byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(_root.BlockSize, 64 * 1024));
                            try
                            {
                                for (int offset = 0; offset < _root.BlockSize; offset += buffer.Length)
                                {
                                    int count = Math.Min(buffer.Length, _root.BlockSize - offset);
                                    Span<byte> chunk = buffer.AsSpan(0, count);
                                    ReadMetadataExactly(regionOffset + offset, chunk);
                                    for (int i = 0; i < count; i += 8)
                                    {
                                        long physical = BinaryPrimitives.ReadInt64LittleEndian(chunk[i..]);
                                        if (physical == 0)
                                        {
                                            continue;
                                        }

                                        ValidatePayloadBlock(physical);
                                        if (!occupied.Add(physical))
                                        {
                                            throw Corruption("Duplicate physical ownership was found during free-space discovery.", physical);
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(buffer);
                            }
                        }
                        finally
                        {
                            _operationGate.Release();
                        }

                        await Task.Yield();
                    }

                    await _operationGate.WaitAsync(_backgroundCancellation.Token).ConfigureAwait(false);
                    try
                    {
                        occupied.UnionWith(_allocatedSinceScan);
                        _allocatedSinceScan.Clear();
                        long snapshotEnd = _nextAppendOffset;
                        bool reachedCapacity = false;
                        for (long candidate = _root.BlockSize;
                             candidate + _root.BlockSize <= snapshotEnd;
                             candidate += _root.BlockSize)
                        {
                            if (_knownFreeBlocks.Count >= _options.FreeBlockQueueCapacity)
                            {
                                reachedCapacity = true;
                                break;
                            }

                            if (!occupied.Contains(candidate) && _knownFreeBlocks.Add(candidate))
                            {
                                _freeBlocks.Enqueue(candidate, candidate);
                            }
                        }

                        _lastExhaustiveScanEnd = reachedCapacity ? 0 : snapshotEnd;
                    }
                    finally
                    {
                        _operationGate.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (_backgroundCancellation.IsCancellationRequested)
            {
            }
            catch (DynamicAllocationStreamCorruptionException exception)
            {
                _backgroundFault = exception;
            }
            catch (IOException)
            {
                // Discovery is opportunistic. Foreground allocation continues by appending.
            }
        });
    }

    private void RequestBackgroundScan()
    {
        if (_backgroundScan is null || _backgroundCancellation.IsCancellationRequested ||
            _knownFreeBlocks.Count > _options.FreeBlockQueueLowWatermark ||
            _lastExhaustiveScanEnd >= _nextAppendOffset)
        {
            return;
        }

        if (_freeScanSignal.CurrentCount == 0)
        {
            _freeScanSignal.Release();
        }
    }

    private void CopyPhysicalBlock(long source, long destination)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(_root.BlockSize, 1024 * 1024));
        try
        {
            int completed = 0;
            while (completed < _root.BlockSize)
            {
                int count = Math.Min(buffer.Length, _root.BlockSize - completed);
                ReadExactlyAt(source + completed, buffer.AsSpan(0, count));
                WriteAt(destination + completed, buffer.AsSpan(0, count));
                completed += count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private bool IsPhysicalBlockZero(long physicalOffset)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(_root.BlockSize, 1024 * 1024));
        try
        {
            int completed = 0;
            while (completed < _root.BlockSize)
            {
                int count = Math.Min(buffer.Length, _root.BlockSize - completed);
                Span<byte> chunk = buffer.AsSpan(0, count);
                ReadExactlyAt(physicalOffset + completed, chunk);
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

    private void ZeroPhysicalBlock(long physicalOffset) => ZeroRange(physicalOffset, _root.BlockSize);

    private ValueTask ZeroPhysicalBlockAsync(long physicalOffset, CancellationToken cancellationToken) =>
        ZeroRangeAsync(physicalOffset, _root.BlockSize, cancellationToken);

    private void ZeroRange(long physicalOffset, long length)
    {
        byte[] zeros = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, 64 * 1024));
        Array.Clear(zeros);
        try
        {
            long completed = 0;
            while (completed < length)
            {
                int count = (int)Math.Min(zeros.Length, length - completed);
                WriteAt(physicalOffset + completed, zeros.AsSpan(0, count));
                completed += count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(zeros);
        }
    }

    private async ValueTask ZeroRangeAsync(long physicalOffset, long length, CancellationToken cancellationToken)
    {
        byte[] zeros = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, 64 * 1024));
        Array.Clear(zeros);
        try
        {
            long completed = 0;
            while (completed < length)
            {
                int count = (int)Math.Min(zeros.Length, length - completed);
                await WriteAtAsync(physicalOffset + completed, zeros.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                completed += count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(zeros);
        }
    }

    private void WriteNewRegionPage(long offset, int length, RegionPage page)
    {
        int prefixLength = DynamicAllocationFormat.RegionHeaderSize +
            (page.Entries.Count * DynamicAllocationFormat.RegionEntrySize);
        byte[] prefix = GC.AllocateUninitializedArray<byte>(prefixLength);
        byte[] link = new byte[DynamicAllocationFormat.RegionEntrySize];
        DynamicAllocationFormat.WriteRegionPageParts(prefix, link, page);
        WriteAt(offset, prefix);
        WriteAt(offset + length - link.Length, link);
    }

    private void WriteInt64At(long offset, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        WriteAt(offset, buffer);
    }

    private ValueTask WriteInt64AtAsync(long offset, long value, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        return WriteAtAsync(offset, buffer, cancellationToken);
    }

    private void WriteAt(long offset, ReadOnlySpan<byte> source)
    {
        if (_underlyingRandomAccess is { CanWriteAt: true } randomAccess)
        {
            randomAccess.WriteAt(source, offset);
            return;
        }

        _underlying.Position = offset;
        _underlying.Write(source);
    }

    private async ValueTask WriteAtAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        if (_underlyingRandomAccess is { CanWriteAt: true } randomAccess)
        {
            await randomAccess.WriteAtAsync(source, offset, cancellationToken).ConfigureAwait(false);
            return;
        }

        _underlying.Position = offset;
        await _underlying.WriteAsync(source, cancellationToken).ConfigureAwait(false);
    }

    private void ReadExactlyAt(long offset, Span<byte> destination)
    {
        if (_underlyingRandomAccess is { CanReadAt: true } randomAccess)
        {
            while (!destination.IsEmpty)
            {
                int read = randomAccess.ReadAt(destination, offset);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                destination = destination[read..];
                offset += read;
            }

            return;
        }

        _underlying.Position = offset;
        _underlying.ReadExactly(destination);
    }

    private async ValueTask ReadExactlyAtAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (_underlyingRandomAccess is { CanReadAt: true } randomAccess)
        {
            while (!destination.IsEmpty)
            {
                int read = await randomAccess.ReadAtAsync(destination, offset, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                destination = destination[read..];
                offset += read;
            }

            return;
        }

        _underlying.Position = offset;
        await _underlying.ReadExactlyAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private void PhysicalBarrier()
    {
        if (_underlying is FileStream fileStream)
        {
            fileStream.Flush(flushToDisk: true);
        }
        else
        {
            _underlying.Flush();
        }
    }

    private async ValueTask PhysicalBarrierAsync(CancellationToken cancellationToken)
    {
        await _underlying.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (_underlying is FileStream fileStream)
        {
            System.IO.RandomAccess.FlushToDisk(fileStream.SafeFileHandle);
        }
    }

    private void ValidatePatchTarget(long offset, long requiredPhysicalLength)
    {
        if (offset < 0 || (offset & 7) != 0 || offset > requiredPhysicalLength - 8)
        {
            throw Corruption("A metadata journal patch target is invalid.", offset);
        }

        bool inIdentifierOrRoots = offset < DynamicAllocationFormat.PrimaryRegionOffset;
        bool inJournal = offset >= _root.JournalOffset && offset < _root.JournalOffset + _root.JournalLength;
        if (inIdentifierOrRoots || inJournal)
        {
            throw Corruption("A metadata journal patch targets a protected header structure.", offset);
        }
    }

    private void ValidatePhysicalBlock(long offset, bool requireComplete)
    {
        if (offset < _root.BlockSize || (offset & (_root.BlockSize - 1L)) != 0)
        {
            throw Corruption("A physical block offset is not valid or aligned.", offset);
        }

        if (requireComplete && (offset > _underlying.Length - _root.BlockSize))
        {
            throw Corruption("A physical block is truncated.", offset);
        }
    }

    private void ValidatePayloadBlock(long offset)
    {
        ValidatePhysicalBlock(offset, requireComplete: true);
        if (_batRegions.ContainsValue(offset) || _trimRegions.ContainsValue(offset) ||
            _dependentRegions.ContainsValue(offset) ||
            _regionPages.Skip(1).Any(page => page.Offset == offset))
        {
            throw Corruption("A BAT entry points to metadata.", offset);
        }
    }

    private void ValidateWriteEnd(long offset, int count)
    {
        if (offset < 0 || offset > _root.VirtualCapacity || count > _root.VirtualCapacity - offset)
        {
            throw new IOException("The write would exceed virtual capacity.");
        }
    }

    private int GetLogicalBlockLength(long logicalBlock)
    {
        long start = checked(logicalBlock * (long)_root.BlockSize);
        return (int)Math.Min(_root.BlockSize, _root.VirtualCapacity - start);
    }

    private long GetLogicalBlockEnd(long logicalBlock) =>
        Math.Min(DynamicAllocationFormat.LogicalBlockEnd(logicalBlock, _root.BlockSize), _root.VirtualCapacity);

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

        if (end > _logicalLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The trim range must lie within logical Length.");
        }
    }

    private static void ValidateCompactionMode(DynamicAllocationCompactionMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void ThrowIfReadOnly()
    {
        if (_readOnly)
        {
            throw new NotSupportedException("The dynamic allocation stream is read-only.");
        }
    }

    private void ThrowBackgroundFault()
    {
        if (_backgroundFault is not null)
        {
            throw new DynamicAllocationStreamCorruptionException(
                "Background free-space discovery found corrupt allocation metadata.",
                null,
                _backgroundFault);
        }
    }

    private static DynamicAllocationStreamCorruptionException Corruption(string message, long? offset) =>
        new(message, offset);

    private sealed class RegionPageLocation
    {
        internal RegionPageLocation(RegionPage page, long offset, int length)
        {
            Page = page;
            Offset = offset;
            Length = length;
        }

        internal RegionPage Page { get; }
        internal long Offset { get; set; }
        internal int Length { get; }
    }

    private readonly record struct AllocatedPayload(long LogicalBlock, long PhysicalOffset, bool Trimmed);

    private enum PhysicalOwnerKind
    {
        Payload,
        BatRegion,
        TrimRegion,
        DependentRegion,
        SubRegion,
    }

    private readonly record struct PhysicalOwner(PhysicalOwnerKind Kind, long LogicalIndex)
    {
        internal static PhysicalOwner ForPayload(long logicalBlock) => new(PhysicalOwnerKind.Payload, logicalBlock);

        internal static PhysicalOwner ForRegion(uint kind, long index) => new(
            kind switch
            {
                DynamicAllocationFormat.BatRegionKind => PhysicalOwnerKind.BatRegion,
                DynamicAllocationFormat.TrimRegionKind => PhysicalOwnerKind.TrimRegion,
                DynamicAllocationFormat.DependentRegionKind => PhysicalOwnerKind.DependentRegion,
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            },
            index);

        internal static PhysicalOwner ForSubRegion(long index) => new(PhysicalOwnerKind.SubRegion, index);
    }
}
