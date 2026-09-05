namespace TeeForge.Experimental.Storage.Tests;

public class SparseDiskImageTests
{
    private const int BlockSize = 64 * 1024;
    private const long VirtualCapacity = 16L * BlockSize;
    private static readonly SparseDiskImageOptions TestOptions = new(
        leaveOpen: true,
        freeBlockQueueCapacity: 0,
        freeBlockQueueLowWatermark: 0);

    [Fact]
    public void Partial_first_write_extends_to_block_end_and_zero_initializes_remainder()
    {
        using var backing = new MemoryStream();
        using SparseDiskImage stream = SparseDiskImage.Create(backing, VirtualCapacity, BlockSize, TestOptions);

        stream.Position = 123;
        stream.Write([1, 2, 3]);

        Assert.Equal(BlockSize, stream.Length);
        stream.Position = 120;
        byte[] actual = new byte[9];
        Assert.Equal(actual.Length, stream.Read(actual));
        Assert.Equal([0, 0, 0, 1, 2, 3, 0, 0, 0], actual);
    }

    [Fact]
    public void Sparse_gap_reads_as_zero_after_reopen()
    {
        using var backing = new MemoryStream();
        Guid id;
        using (SparseDiskImage created = SparseDiskImage.Create(backing, VirtualCapacity, BlockSize, TestOptions))
        {
            id = created.Id;
            created.Position = (2L * BlockSize) + 17;
            created.Write([7, 8, 9]);
            created.Flush();
        }

        backing.Position = 0;
        using SparseDiskImage opened = SparseDiskImage.Open(backing, TestOptions);
        Assert.Equal(id, opened.Id);
        Assert.Equal(3L * BlockSize, opened.Length);
        opened.Position = BlockSize - 2;
        byte[] actual = new byte[BlockSize + 22];
        opened.ReadExactly(actual);
        Assert.All(actual.AsSpan(0, BlockSize + 19).ToArray(), value => Assert.Equal(0, value));
        Assert.Equal([7, 8, 9], actual.AsSpan(BlockSize + 19, 3).ToArray());
    }

    [Fact]
    public void Full_block_trim_discards_data_and_lowers_tail_length()
    {
        using var backing = new MemoryStream();
        using SparseDiskImage stream = SparseDiskImage.Create(backing, VirtualCapacity, BlockSize, TestOptions);
        stream.Write(new byte[BlockSize]);
        stream.Write(Enumerable.Repeat((byte)42, BlockSize).ToArray());

        stream.Trim(BlockSize, BlockSize);

        Assert.Equal(BlockSize, stream.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Trim(BlockSize, 1));
    }

    [Fact]
    public void Partial_trim_zeroes_only_requested_bytes()
    {
        using var backing = new MemoryStream();
        using SparseDiskImage stream = SparseDiskImage.Create(backing, VirtualCapacity, BlockSize, TestOptions);
        stream.Write(Enumerable.Repeat((byte)9, BlockSize).ToArray());

        stream.Trim(100, 200);
        stream.Position = 96;
        byte[] actual = new byte[208];
        stream.ReadExactly(actual);

        Assert.Equal([9, 9, 9, 9], actual[..4]);
        Assert.All(actual[4..204], value => Assert.Equal(0, value));
        Assert.Equal([9, 9, 9, 9], actual[204..]);
    }

    [Fact]
    public void Partial_write_to_trimmed_block_does_not_restore_discarded_bytes()
    {
        using var backing = new MemoryStream();
        using SparseDiskImage stream = SparseDiskImage.Create(backing, VirtualCapacity, BlockSize, TestOptions);
        stream.Write(Enumerable.Repeat((byte)31, BlockSize).ToArray());
        stream.Trim(0, BlockSize);

        stream.Position = 50;
        stream.Write([1, 2, 3]);
        stream.Position = 0;
        byte[] actual = new byte[64];
        stream.ReadExactly(actual);

        Assert.All(actual[..50], value => Assert.Equal(0, value));
        Assert.Equal([1, 2, 3], actual[50..53]);
        Assert.All(actual[53..], value => Assert.Equal(0, value));
    }

    [Fact]
    public void Forced_read_only_mode_allows_reads_and_rejects_mutation()
    {
        using var backing = new MemoryStream();
        using (SparseDiskImage created = SparseDiskImage.Create(backing, VirtualCapacity, BlockSize, TestOptions))
        {
            created.Write([1, 2, 3]);
            created.Flush();
        }

        var readOnlyOptions = new SparseDiskImageOptions(
            leaveOpen: true,
            readOnly: true,
            freeBlockQueueCapacity: 0,
            freeBlockQueueLowWatermark: 0);
        using SparseDiskImage opened = SparseDiskImage.Open(backing, readOnlyOptions);

        Assert.True(opened.IsReadOnly);
        Assert.False(opened.CanWrite);
        Assert.Equal(1, opened.ReadByte());
        Assert.Throws<NotSupportedException>(() => opened.WriteByte(4));
        Assert.Throws<NotSupportedException>(() => opened.Trim(0, 1));
        Assert.Throws<NotSupportedException>(() => opened.Compact());
    }

    [Fact]
    public void Active_journal_is_replayed_after_interrupted_home_write()
    {
        using var storage = new MemoryStream();
        using var faulting = new FaultingWriteStream(storage);
        SparseDiskImage stream = SparseDiskImage.Create(faulting, VirtualCapacity, BlockSize, TestOptions);
        stream.Position = BlockSize;
        stream.Write([4, 5, 6]);
        faulting.ThrowOnWriteNumber = 3;

        Assert.Throws<InjectedFailureException>(() => stream.Flush());

        faulting.ThrowOnWriteNumber = 0;
        storage.Position = 0;
        using SparseDiskImage recovered = SparseDiskImage.Open(storage, TestOptions);
        recovered.Position = BlockSize;
        Assert.Equal([4, 5, 6], ReadExactly(recovered, 3));
        Assert.Equal(2L * BlockSize, recovered.Length);
    }

    [Fact]
    public void Active_journal_can_be_replayed_into_read_only_overlay()
    {
        using var storage = new MemoryStream();
        using var faulting = new FaultingWriteStream(storage);
        SparseDiskImage stream = SparseDiskImage.Create(faulting, VirtualCapacity, BlockSize, TestOptions);
        stream.Position = BlockSize;
        stream.Write([11, 12, 13]);
        faulting.ThrowOnWriteNumber = 3;
        Assert.Throws<InjectedFailureException>(() => stream.Flush());

        using var readOnlyBacking = new MemoryStream(storage.ToArray(), writable: false);
        using SparseDiskImage recovered = SparseDiskImage.Open(readOnlyBacking, TestOptions);

        Assert.True(recovered.IsReadOnly);
        Assert.Equal(2L * BlockSize, recovered.Length);
        recovered.Position = BlockSize;
        Assert.Equal([11, 12, 13], ReadExactly(recovered, 3));
    }

    [Fact]
    public async Task Async_write_flush_open_and_read_round_trip()
    {
        using var backing = new MemoryStream();
        await using (SparseDiskImage created = await SparseDiskImage.CreateAsync(
            backing,
            VirtualCapacity,
            BlockSize,
            TestOptions,
            TestContext.Current.CancellationToken))
        {
            created.Position = BlockSize - 2;
            await created.WriteAsync(new byte[] { 1, 2, 3, 4 }, TestContext.Current.CancellationToken);
            await created.FlushAsync(TestContext.Current.CancellationToken);
        }

        await using SparseDiskImage opened = await SparseDiskImage.OpenAsync(
            backing,
            TestOptions,
            TestContext.Current.CancellationToken);
        opened.Position = BlockSize - 2;
        byte[] actual = new byte[4];
        Assert.Equal(4, await opened.ReadAsync(actual, TestContext.Current.CancellationToken));
        Assert.Equal([1, 2, 3, 4], actual);
    }

    [Fact]
    public void Fast_compaction_reclaims_trimmed_tail_and_truncates_backing_stream()
    {
        using var backing = new MemoryStream();
        using SparseDiskImage stream = SparseDiskImage.Create(backing, VirtualCapacity, BlockSize, TestOptions);
        stream.Write(Enumerable.Repeat((byte)1, BlockSize).ToArray());
        stream.Write(Enumerable.Repeat((byte)2, BlockSize).ToArray());
        stream.Flush();
        long before = backing.Length;
        stream.Trim(BlockSize, BlockSize);

        long after = stream.Compact();

        Assert.True(after < before);
        Assert.Equal(BlockSize, stream.Length);
        stream.Position = 0;
        Assert.All(ReadExactly(stream, 128), value => Assert.Equal(1, value));
    }

    [Fact]
    public void Slow_compaction_reclaims_zero_tail_blocks_without_zero_scan_in_estimate()
    {
        using var backing = new MemoryStream();
        using SparseDiskImage stream = SparseDiskImage.Create(backing, VirtualCapacity, BlockSize, TestOptions);
        stream.Write(Enumerable.Repeat((byte)1, BlockSize).ToArray());
        stream.Write(new byte[BlockSize]);
        stream.Flush();
        long estimate = stream.EstimateCompactionSavings();

        long before = backing.Length;
        long after = stream.Compact(DynamicAllocationCompactionMode.Slow);

        Assert.Equal(0, estimate);
        Assert.True(after < before);
        Assert.Equal(BlockSize, stream.Length);
    }

    [Fact]
    public void Compaction_moves_live_payload_into_earlier_hole_without_changing_logical_data()
    {
        using var backing = new MemoryStream();
        using SparseDiskImage stream = SparseDiskImage.Create(backing, VirtualCapacity, BlockSize, TestOptions);
        stream.Write(Enumerable.Repeat((byte)1, BlockSize).ToArray());
        stream.Write(Enumerable.Repeat((byte)2, BlockSize).ToArray());
        stream.Write(Enumerable.Repeat((byte)3, BlockSize).ToArray());
        stream.Trim(BlockSize, BlockSize);
        stream.Flush();
        long before = backing.Length;

        long after = stream.Compact();

        Assert.True(after < before);
        Assert.Equal(3L * BlockSize, stream.Length);
        stream.Position = 0;
        Assert.All(ReadExactly(stream, 128), value => Assert.Equal(1, value));
        stream.Position = BlockSize;
        Assert.All(ReadExactly(stream, 128), value => Assert.Equal(0, value));
        stream.Position = 2L * BlockSize;
        Assert.All(ReadExactly(stream, 128), value => Assert.Equal(3, value));
    }

    [Fact]
    public void Sequential_copy_requires_no_logical_seek()
    {
        using var backing = new MemoryStream();
        using SparseDiskImage stream = SparseDiskImage.Create(backing, VirtualCapacity, BlockSize, TestOptions);
        byte[] expected = Enumerable.Range(0, BlockSize + 17).Select(index => (byte)index).ToArray();
        stream.Write(expected);
        stream.Position = 0;
        using var destination = new MemoryStream();

        stream.CopyTo(destination);

        Assert.Equal(stream.Length, destination.Length);
        Assert.Equal(expected, destination.ToArray()[..expected.Length]);
    }

    [Fact]
    public void Invalid_magic_and_roots_are_rejected()
    {
        using var backing = new MemoryStream(new byte[BlockSize], writable: true);
        Assert.Throws<SparseDiskImageCorruptionException>(() => SparseDiskImage.Open(backing, TestOptions));
    }

    [Fact]
    public void One_corrupt_root_falls_back_to_other_valid_copy()
    {
        using var backing = new MemoryStream();
        using (SparseDiskImage stream = SparseDiskImage.Create(backing, VirtualCapacity, BlockSize, TestOptions))
        {
            Assert.Equal(0, stream.Length);
        }

        backing.Position = 8192 + 20;
        backing.WriteByte(0xFF);
        backing.Position = 0;

        using SparseDiskImage opened = SparseDiskImage.Open(backing, TestOptions);
        Assert.Equal(0, opened.Length);
    }

    private static byte[] ReadExactly(Stream stream, int count)
    {
        byte[] result = new byte[count];
        stream.ReadExactly(result);
        return result;
    }

    private sealed class FaultingWriteStream(Stream inner) : Stream
    {
        private int _writeCount;

        private int _throwOnWriteNumber;

        internal int ThrowOnWriteNumber
        {
            get => _throwOnWriteNumber;
            set
            {
                _throwOnWriteNumber = value;
                _writeCount = 0;
            }
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            MaybeThrow();
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            MaybeThrow();
            inner.Write(buffer);
        }

        private void MaybeThrow()
        {
            _writeCount++;
            if (_throwOnWriteNumber > 0 && _writeCount == _throwOnWriteNumber)
            {
                throw new InjectedFailureException();
            }
        }
    }

    private sealed class InjectedFailureException : IOException;
}
