using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

namespace TeeForge.Benchmarks;

/// <summary>
/// Compares ordinary steady-state operations through HandoffStream with the same operations sent
/// directly to a non-copying observing stream. Each invocation performs two operations over the
/// same buffers, which isolates synchronization and delegation from storage or memory bandwidth.
/// Handoff itself is deliberately outside the measured path.
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 3, iterationCount: 8)]
public class HandoffStreamBenchmarks : IDisposable
{
    private ObservingStream _destination = null!;
    private HandoffStream _handoff = null!;
    private byte[] _payload = null!;
    private byte[] _readBuffer = null!;
    private readonly SemaphoreSlim _synchronizedGate = new(1, 1);

    [Params(4 * 1024, 64 * 1024, 1024 * 1024)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _payload = GC.AllocateUninitializedArray<byte>(PayloadSize);
        Random.Shared.NextBytes(_payload);
        _readBuffer = GC.AllocateUninitializedArray<byte>(PayloadSize);
        _destination = new ObservingStream(_payload);
        _handoff = new HandoffStream(_destination, leaveOpen: true);
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _handoff?.Dispose();
        _destination?.Dispose();
        _synchronizedGate.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = 2)]
    [BenchmarkCategory("SequentialWrite")]
    public void DirectWrite()
    {
        _destination.Write(_payload);
        _destination.Write(_payload);
    }

    [Benchmark(OperationsPerInvoke = 2)]
    [BenchmarkCategory("SequentialWrite")]
    public void HandoffWrite()
    {
        _destination.Write(_payload);
        _handoff.Write(_payload);
    }

    [Benchmark(OperationsPerInvoke = 2)]
    [BenchmarkCategory("SequentialWrite")]
    public void ManuallySynchronizedWrite()
    {
        _destination.Write(_payload);
        _synchronizedGate.Wait();
        try
        {
            _destination.Write(_payload);
        }
        finally
        {
            _synchronizedGate.Release();
        }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = 2)]
    [BenchmarkCategory("SequentialWriteAsync")]
    public async ValueTask DirectWriteAsync()
    {
        await _destination.WriteAsync(_payload.AsMemory()).ConfigureAwait(false);
        await _destination.WriteAsync(_payload.AsMemory()).ConfigureAwait(false);
    }

    [Benchmark(OperationsPerInvoke = 2)]
    [BenchmarkCategory("SequentialWriteAsync")]
    public async ValueTask HandoffWriteAsync()
    {
        await _destination.WriteAsync(_payload.AsMemory()).ConfigureAwait(false);
        await _handoff.WriteAsync(_payload.AsMemory()).ConfigureAwait(false);
    }

    [Benchmark(OperationsPerInvoke = 2)]
    [BenchmarkCategory("SequentialWriteAsync")]
    public async ValueTask ManuallySynchronizedWriteAsync()
    {
        await _destination.WriteAsync(_payload.AsMemory()).ConfigureAwait(false);
        await _synchronizedGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _destination.WriteAsync(_payload.AsMemory()).ConfigureAwait(false);
        }
        finally
        {
            _synchronizedGate.Release();
        }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = 2)]
    [BenchmarkCategory("RandomAccessRead")]
    public int DirectReadAt()
    {
        int first = _destination.ReadAt(_readBuffer, offset: 0);
        int second = _destination.ReadAt(_readBuffer, offset: 0);
        return first ^ second ^ _readBuffer[0];
    }

    [Benchmark(OperationsPerInvoke = 2)]
    [BenchmarkCategory("RandomAccessRead")]
    public int HandoffReadAt()
    {
        int direct = _destination.ReadAt(_readBuffer, offset: 0);
        int handoff = _handoff.ReadAt(_readBuffer, offset: 0);
        return direct ^ handoff ^ _readBuffer[0];
    }

    [Benchmark(OperationsPerInvoke = 2)]
    [BenchmarkCategory("RandomAccessRead")]
    public int ManuallySynchronizedReadAt()
    {
        int direct = _destination.ReadAt(_readBuffer, offset: 0);
        _synchronizedGate.Wait();
        try
        {
            int synchronized = _destination.ReadAt(_readBuffer, offset: 0);
            return direct ^ synchronized ^ _readBuffer[0];
        }
        finally
        {
            _synchronizedGate.Release();
        }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = 2)]
    [BenchmarkCategory("RandomAccessWrite")]
    public void DirectWriteAt()
    {
        _destination.WriteAt(_payload, offset: 0);
        _destination.WriteAt(_payload, offset: 0);
    }

    [Benchmark(OperationsPerInvoke = 2)]
    [BenchmarkCategory("RandomAccessWrite")]
    public void HandoffWriteAt()
    {
        _destination.WriteAt(_payload, offset: 0);
        _handoff.WriteAt(_payload, offset: 0);
    }

    [Benchmark(OperationsPerInvoke = 2)]
    [BenchmarkCategory("RandomAccessWrite")]
    public void ManuallySynchronizedWriteAt()
    {
        _destination.WriteAt(_payload, offset: 0);
        _synchronizedGate.Wait();
        try
        {
            _destination.WriteAt(_payload, offset: 0);
        }
        finally
        {
            _synchronizedGate.Release();
        }
    }

    private sealed class ObservingStream : Stream, ITeeRandomAccessStream
    {
        private readonly int _length;
        private volatile int _checksum;

        public ObservingStream(ReadOnlySpan<byte> initialData)
        {
            _length = initialData.Length;
            _checksum = ComputeChecksum(initialData);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public bool CanReadAt => true;

        public bool CanWriteAt => true;

        public override long Length => _length;

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        [MethodImpl(MethodImplOptions.NoInlining)]
        public override int Read(Span<byte> buffer) => ObserveRead(buffer);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadAt(Span<byte> buffer, long offset)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            return ObserveRead(buffer);
        }

        public ValueTask<int> ReadAtAsync(
            Memory<byte> buffer,
            long offset,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReadAt(buffer.Span, offset));
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        [MethodImpl(MethodImplOptions.NoInlining)]
        public override void Write(ReadOnlySpan<byte> buffer) => ObserveWrite(buffer);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void WriteAt(ReadOnlySpan<byte> buffer, long offset)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ObserveWrite(buffer);
        }

        public ValueTask WriteAtAsync(
            ReadOnlyMemory<byte> buffer,
            long offset,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteAt(buffer.Span, offset);
            return ValueTask.CompletedTask;
        }

        private int ObserveRead(Span<byte> destination)
        {
            int count = Math.Min(destination.Length, _length);
            if (count != 0)
            {
                destination[0] = (byte)_checksum;
                destination[count - 1] = (byte)(_checksum >> 8);
            }

            return count;
        }

        private void ObserveWrite(ReadOnlySpan<byte> source) =>
            _checksum = ComputeChecksum(source[..Math.Min(source.Length, _length)]);

        private static int ComputeChecksum(ReadOnlySpan<byte> buffer) =>
            buffer.Length == 0 ? 0 : buffer[0] ^ buffer[^1] ^ buffer.Length;
    }
}
