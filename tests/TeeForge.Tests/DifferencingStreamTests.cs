namespace TeeForge.Tests;

public class DifferencingStreamTests
{
    private const int BlockSize = 64 * 1024;
    private const long Capacity = 4L * BlockSize;
    private static readonly DynamicAllocationStreamOptions DynamicOptions = new(
        leaveOpen: true,
        freeBlockQueueCapacity: 0,
        freeBlockQueueLowWatermark: 0);
    private static readonly DifferencingStreamOptions DifferenceOptions = new(
        leaveBaseOpen: true,
        leaveDifferenceOpen: true);

    [Fact]
    public void Inherited_read_matches_base_and_partial_write_changes_only_child_grain()
    {
        using var baseStorage = new MemoryStream();
        using DynamicAllocationStream baseDisk = CreateBase(baseStorage);
        byte[] baseBytes = Enumerable.Range(0, BlockSize).Select(static index => (byte)index).ToArray();
        baseDisk.Write(baseBytes);
        baseDisk.Flush();
        Guid baseDataWriteId = baseDisk.DataWriteId;
        using var differenceStorage = new MemoryStream();
        using DifferencingStream child = DifferencingStream.Create(
            baseDisk,
            differenceStorage,
            DifferenceOptions,
            "..\\base.tfdisk");

        byte[] inherited = new byte[32];
        Assert.Equal(32, child.ReadAt(inherited, 4000));
        Assert.Equal(baseBytes.AsSpan(4000, 32).ToArray(), inherited);

        child.WriteAt([91, 92, 93], 4094);

        byte[] overlaid = new byte[12];
        Assert.Equal(12, child.ReadAt(overlaid, 4090));
        Assert.Equal(baseBytes.AsSpan(4090, 4).ToArray(), overlaid[..4]);
        Assert.Equal([91, 92, 93], overlaid[4..7]);
        Assert.Equal(baseBytes.AsSpan(4097, 5).ToArray(), overlaid[7..]);
        Assert.Equal(baseDataWriteId, baseDisk.DataWriteId);
        Assert.Equal(baseBytes.AsSpan(4094, 3).ToArray(), ReadAt(baseDisk, 4094, 3));
        Assert.Equal("..\\base.tfdisk", child.ParentPathHint);
    }

    [Fact]
    public async Task Locator_is_validated_by_library_and_preserves_stream_position()
    {
        using var baseStorage = new MemoryStream();
        using DynamicAllocationStream baseDisk = CreateBase(baseStorage);
        using var differenceStorage = new MemoryStream();
        using (DifferencingStream child = DifferencingStream.Create(
            baseDisk,
            differenceStorage,
            DifferenceOptions,
            "..\\base.tfdisk"))
        {
            child.Flush();
        }

        differenceStorage.Position = 123;
        DifferencingStreamLocator locator = await DifferencingStream.ReadLocatorAsync(differenceStorage);

        Assert.Equal(123, differenceStorage.Position);
        Assert.Equal(baseDisk.Id, locator.BaseId);
        Assert.Equal(baseDisk.DataWriteId, locator.BaseDataWriteId);
        Assert.Equal(Capacity, locator.VirtualCapacity);
        Assert.Equal(BlockSize, locator.BlockSize);
        Assert.Equal("..\\base.tfdisk", locator.ParentPathHint);

        byte[] corrupt = differenceStorage.ToArray();
        corrupt[56] ^= 1;
        using var corruptStorage = new MemoryStream(corrupt);
        Assert.Throws<IOException>(() => DifferencingStream.ReadLocator(corruptStorage));
    }

    [Fact]
    public void Partial_state_and_erased_tail_survive_reopen()
    {
        using var baseStorage = new MemoryStream();
        using DynamicAllocationStream baseDisk = CreateBase(baseStorage);
        baseDisk.Write(Enumerable.Repeat((byte)7, 2 * BlockSize).ToArray());
        baseDisk.Flush();
        using var differenceStorage = new MemoryStream();
        Guid childId;
        Guid childDataWriteId;
        using (DifferencingStream child = DifferencingStream.Create(baseDisk, differenceStorage, DifferenceOptions))
        {
            childId = child.Id;
            child.WriteAt([1, 2, 3], 123);
            child.Trim(BlockSize, BlockSize);
            child.Flush();
            childDataWriteId = child.DataWriteId;
            Assert.Equal(BlockSize, child.Length);
        }

        differenceStorage.Position = 0;
        using DifferencingStream reopened = DifferencingStream.Open(baseDisk, differenceStorage, DifferenceOptions);

        Assert.Equal(childId, reopened.Id);
        Assert.Equal(childDataWriteId, reopened.DataWriteId);
        Assert.Equal(BlockSize, reopened.Length);
        Assert.Equal([1, 2, 3], ReadAt(reopened, 123, 3));
        Assert.All(ReadAt(reopened, BlockSize - 16, 16), static value => Assert.Equal(7, value));
    }

    [Fact]
    public void Partial_trim_zeroes_requested_bytes_without_revealing_base()
    {
        using var baseStorage = new MemoryStream();
        using DynamicAllocationStream baseDisk = CreateBase(baseStorage);
        baseDisk.Write(Enumerable.Repeat((byte)44, BlockSize).ToArray());
        baseDisk.Flush();
        using var differenceStorage = new MemoryStream();
        using DifferencingStream child = DifferencingStream.Create(baseDisk, differenceStorage, DifferenceOptions);

        child.Trim(4093, 7);

        byte[] actual = ReadAt(child, 4088, 20);
        Assert.All(actual[..5], static value => Assert.Equal(44, value));
        Assert.All(actual[5..12], static value => Assert.Equal(0, value));
        Assert.All(actual[12..], static value => Assert.Equal(44, value));
        child.WriteAt([8], 4095);
        Assert.Equal([0, 0, 8, 0, 0, 0, 0], ReadAt(child, 4093, 7));
    }

    [Fact]
    public async Task Explicit_offset_async_io_does_not_change_position()
    {
        using var baseStorage = new MemoryStream();
        using DynamicAllocationStream baseDisk = CreateBase(baseStorage);
        baseDisk.Write(new byte[BlockSize]);
        baseDisk.Flush();
        using var differenceStorage = new MemoryStream();
        await using DifferencingStream child = DifferencingStream.Create(baseDisk, differenceStorage, DifferenceOptions);
        child.Position = 77;

        await child.WriteAtAsync(new byte[] { 4, 5, 6 }, BlockSize + 9);
        byte[] actual = new byte[3];
        Assert.Equal(3, await child.ReadAtAsync(actual, BlockSize + 9));

        Assert.Equal([4, 5, 6], actual);
        Assert.Equal(77, child.Position);
    }

    [Fact]
    public async Task Async_write_and_trim_do_not_fall_back_to_synchronous_difference_io()
    {
        using var baseStorage = new MemoryStream();
        using DynamicAllocationStream baseDisk = CreateBase(baseStorage);
        baseDisk.Write(Enumerable.Repeat((byte)12, BlockSize).ToArray());
        baseDisk.Flush();
        using var differenceStorage = new AsyncTrackingMemoryStream();
        await using DifferencingStream child = DifferencingStream.Create(baseDisk, differenceStorage, DifferenceOptions);
        differenceStorage.ResetTracking();

        await child.WriteAtAsync(new byte[] { 4, 5, 6 }, 4094);
        await child.TrimAsync(4095, 1);

        Assert.Equal(0, differenceStorage.SynchronousWriteCount);
        Assert.True(differenceStorage.AsynchronousWriteCount > 0);
        Assert.Equal([4, 0, 6], ReadAt(child, 4094, 3));
    }

    [Fact]
    public void Open_rejects_changed_base_data_identity()
    {
        using var baseStorage = new MemoryStream();
        using DynamicAllocationStream baseDisk = CreateBase(baseStorage);
        baseDisk.Write([1]);
        baseDisk.Flush();
        using var differenceStorage = new MemoryStream();
        using (DifferencingStream child = DifferencingStream.Create(baseDisk, differenceStorage, DifferenceOptions))
        {
            child.Flush();
        }

        baseStorage.Position = 0;
        using DynamicAllocationStream reopenedBase = DynamicAllocationStream.Open(baseStorage, DynamicOptions);
        reopenedBase.WriteAt([2], 0);
        reopenedBase.Flush();

        Assert.Throws<DifferencingStreamBaseMismatchException>(() =>
            DifferencingStream.Open(reopenedBase, differenceStorage, DifferenceOptions));
    }

    [Fact]
    public void Notify_on_create_registers_immediate_child_without_changing_base_data_identity()
    {
        using var baseStorage = new MemoryStream();
        using DynamicAllocationStream baseDisk = CreateBase(baseStorage);
        Guid before = baseDisk.DataWriteId;
        using var differenceStorage = new MemoryStream();
        var options = new DifferencingStreamOptions(
            leaveBaseOpen: true,
            leaveDifferenceOpen: true,
            notifyBaseOnCreate: true);

        using DifferencingStream child = DifferencingStream.Create(baseDisk, differenceStorage, options);

        Assert.Contains(child.Id, baseDisk.DependentStreamIds);
        Assert.Equal(before, baseDisk.DataWriteId);
    }

    [Fact]
    public void Differencing_chain_reads_parent_overlay_and_writes_only_leaf()
    {
        using var baseStorage = new MemoryStream();
        using DynamicAllocationStream baseDisk = CreateBase(baseStorage);
        baseDisk.Write(Enumerable.Repeat((byte)3, BlockSize).ToArray());
        baseDisk.Flush();
        using var childStorage = new MemoryStream();
        using DifferencingStream child = DifferencingStream.Create(baseDisk, childStorage, DifferenceOptions);
        child.WriteAt([7], 100);
        child.Flush();
        Guid childDataWriteId = child.DataWriteId;
        using var leafStorage = new MemoryStream();
        using DifferencingStream leaf = DifferencingStream.Create(child, leafStorage, DifferenceOptions);

        Assert.Equal([7], ReadAt(leaf, 100, 1));
        leaf.WriteAt([9], 101);

        Assert.Equal([7, 9], ReadAt(leaf, 100, 2));
        Assert.Equal([7, 3], ReadAt(child, 100, 2));
        Assert.Equal(childDataWriteId, child.DataWriteId);
    }

    [Fact]
    public void Dynamic_capacity_and_registry_persist_without_changing_data_identity()
    {
        using var storage = new MemoryStream();
        Guid dependentId = Guid.NewGuid();
        Guid dataWriteId;
        using (DynamicAllocationStream disk = CreateBase(storage))
        {
            dataWriteId = disk.DataWriteId;
            disk.RegisterDependentStream(dependentId);
            disk.RegisterDependentStream(dependentId);
            Assert.Equal(dataWriteId, disk.DataWriteId);
            Assert.Equal(Capacity, disk.VirtualCapacity);
        }

        storage.Position = 0;
        using DynamicAllocationStream reopened = DynamicAllocationStream.Open(storage, DynamicOptions);
        Assert.Equal(Capacity, reopened.VirtualCapacity);
        Assert.Equal(dataWriteId, reopened.DataWriteId);
        Assert.Equal([dependentId], reopened.DependentStreamIds);
        reopened.UnregisterDependentStream(dependentId);
        reopened.UnregisterDependentStream(dependentId);
        Assert.False(reopened.HasDependentStreams);
        Assert.Equal(dataWriteId, reopened.DataWriteId);
    }

    [Fact]
    public void Writes_crossing_capacity_fail_before_allocating_child_storage()
    {
        using var baseStorage = new MemoryStream();
        using DynamicAllocationStream baseDisk = CreateBase(baseStorage);
        using var differenceStorage = new MemoryStream();
        using DifferencingStream child = DifferencingStream.Create(baseDisk, differenceStorage, DifferenceOptions);
        long physicalLength = differenceStorage.Length;

        Assert.Throws<IOException>(() => child.WriteAt([1, 2], Capacity - 1));

        Assert.Equal(physicalLength, differenceStorage.Length);
        Assert.Throws<IOException>(() => baseDisk.WriteAt([1], Capacity));
    }

    [Fact]
    public void Fast_compaction_rebuilds_live_state_and_registry_and_survives_reopen()
    {
        using var baseStorage = new MemoryStream();
        using DynamicAllocationStream baseDisk = CreateBase(baseStorage);
        baseDisk.Write(Enumerable.Repeat((byte)22, 3 * BlockSize).ToArray());
        baseDisk.Flush();
        Guid baseDataWriteId = baseDisk.DataWriteId;
        using var differenceStorage = new MemoryStream();
        Guid childDataWriteId;
        Guid dependentId = Guid.NewGuid();
        byte[] expected;
        long compactedLength;
        using (DifferencingStream child = DifferencingStream.Create(baseDisk, differenceStorage, DifferenceOptions))
        {
            for (int value = 1; value <= 12; value++)
            {
                child.WriteAt([(byte)value], 100);
            }

            child.Trim(BlockSize, BlockSize);
            child.WriteAt([99], (2 * BlockSize) + 200);
            child.RegisterDependentStream(dependentId);
            Guid removedId = Guid.NewGuid();
            child.RegisterDependentStream(removedId);
            child.UnregisterDependentStream(removedId);
            child.Flush();
            expected = ReadAt(child, 0, checked((int)child.Length));
            childDataWriteId = child.DataWriteId;
            long before = differenceStorage.Length;

            Assert.True(child.EstimateCompactionSavings() > 0);
            compactedLength = child.Compact();

            Assert.Equal(compactedLength, differenceStorage.Length);
            Assert.True(compactedLength < before);
            Assert.Equal(expected, ReadAt(child, 0, expected.Length));
            Assert.Equal(childDataWriteId, child.DataWriteId);
            Assert.Equal([dependentId], child.DependentStreamIds);
            Assert.Equal(baseDataWriteId, baseDisk.DataWriteId);
        }

        differenceStorage.Position = 0;
        using DifferencingStream reopened = DifferencingStream.Open(baseDisk, differenceStorage, DifferenceOptions);
        Assert.Equal(compactedLength, differenceStorage.Length);
        Assert.Equal(expected, ReadAt(reopened, 0, expected.Length));
        Assert.Equal(childDataWriteId, reopened.DataWriteId);
        Assert.Equal([dependentId], reopened.DependentStreamIds);
    }

    [Fact]
    public void Slow_compaction_converts_only_logically_zero_child_blocks_to_erased()
    {
        using var baseStorage = new MemoryStream();
        using DynamicAllocationStream baseDisk = CreateBase(baseStorage);
        byte[] baseBlock = Enumerable.Repeat((byte)55, BlockSize).ToArray();
        baseDisk.Write(baseBlock);
        baseDisk.Write(baseBlock);
        baseDisk.Flush();
        using var differenceStorage = new MemoryStream();
        using DifferencingStream child = DifferencingStream.Create(baseDisk, differenceStorage, DifferenceOptions);
        child.WriteAt(new byte[BlockSize], 0);
        child.WriteAt(baseBlock, BlockSize);
        Guid dataWriteId = child.DataWriteId;

        long compactedLength = child.Compact(DynamicAllocationCompactionMode.Slow);

        Assert.Equal(4L * BlockSize, compactedLength);
        Assert.All(ReadAt(child, 0, BlockSize), static value => Assert.Equal(0, value));
        Assert.Equal(baseBlock, ReadAt(child, BlockSize, BlockSize));
        Assert.Equal(dataWriteId, child.DataWriteId);
    }

    [Fact]
    public void Compaction_remains_openable_at_every_injected_write_failure()
    {
        using var baseStorage = new MemoryStream();
        using DynamicAllocationStream baseDisk = CreateBase(baseStorage);
        baseDisk.Write(Enumerable.Repeat((byte)42, 3 * BlockSize).ToArray());
        baseDisk.Flush();
        byte[] pristine;
        byte[] expected;
        using (var differenceStorage = new MemoryStream())
        {
            using (DifferencingStream child = DifferencingStream.Create(baseDisk, differenceStorage, DifferenceOptions))
            {
                for (int index = 0; index < 8; index++)
                {
                    child.WriteAt([(byte)(80 + index)], 100 + index);
                }

                child.Trim(BlockSize, BlockSize);
                child.WriteAt([9], (2 * BlockSize) + 10);
                child.RegisterDependentStream(Guid.NewGuid());
                child.Flush();
                expected = ReadAt(child, 0, checked((int)child.Length));
            }

            pristine = differenceStorage.ToArray();
        }

        int compactionWriteCount;
        using (var inner = CreateExpandableCopy(pristine))
        using (var counter = new FaultingStream(inner, failAtWrite: null))
        using (DifferencingStream child = DifferencingStream.Open(baseDisk, counter, DifferenceOptions))
        {
            child.Compact();
            compactionWriteCount = counter.WriteCount;
        }

        Assert.True(compactionWriteCount > 0);
        for (int failAt = 1; failAt <= compactionWriteCount; failAt++)
        {
            using var inner = CreateExpandableCopy(pristine);
            using (var faulting = new FaultingStream(inner, failAt))
            using (DifferencingStream child = DifferencingStream.Open(baseDisk, faulting, DifferenceOptions))
            {
                Assert.Throws<IOException>(() => child.Compact());
            }

            inner.Position = 0;
            using DifferencingStream reopened = DifferencingStream.Open(baseDisk, inner, DifferenceOptions);
            Assert.Equal(expected, ReadAt(reopened, 0, expected.Length));
        }
    }

    private static DynamicAllocationStream CreateBase(MemoryStream storage) =>
        DynamicAllocationStream.Create(storage, Capacity, BlockSize, DynamicOptions);

    private static byte[] ReadAt(ITeeRandomAccessStream stream, long offset, int count)
    {
        byte[] result = new byte[count];
        Assert.Equal(count, stream.ReadAt(result, offset));
        return result;
    }

    private static MemoryStream CreateExpandableCopy(byte[] source)
    {
        var result = new MemoryStream(source.Length * 2);
        result.Write(source);
        result.Position = 0;
        return result;
    }

    private sealed class AsyncTrackingMemoryStream : Stream
    {
        private readonly MemoryStream _inner = new();

        internal int SynchronousWriteCount { get; private set; }

        internal int AsynchronousWriteCount { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            SynchronousWriteCount++;
            _inner.Write(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            SynchronousWriteCount++;
            _inner.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            AsynchronousWriteCount++;
            return _inner.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        internal void ResetTracking()
        {
            SynchronousWriteCount = 0;
            AsynchronousWriteCount = 0;
        }
    }

    private sealed class FaultingStream(MemoryStream inner, int? failAtWrite) : Stream
    {
        internal int WriteCount { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowIfSelectedWrite();
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ThrowIfSelectedWrite();
            inner.Write(buffer);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }

        private void ThrowIfSelectedWrite()
        {
            WriteCount++;
            if (WriteCount == failAtWrite)
            {
                throw new IOException("Injected compaction write failure.");
            }
        }
    }
}
