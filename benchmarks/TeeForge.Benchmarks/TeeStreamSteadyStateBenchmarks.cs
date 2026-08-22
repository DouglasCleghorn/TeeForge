using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TeeForge.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 3, iterationCount: 8)]
public class TeeStreamSteadyStateBenchmarks : IDisposable
{
    private Stream[] _manualDestinations = null!;
    private byte[] _payload = null!;
    private TeeStream _tee = null!;
    private Stream[] _teeDestinations = null!;

    [Params(4 * 1024, 64 * 1024, 1024 * 1024)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _payload = GC.AllocateUninitializedArray<byte>(PayloadSize);
        _manualDestinations = [new FixedBufferStream(PayloadSize), new FixedBufferStream(PayloadSize)];
        _teeDestinations = [new FixedBufferStream(PayloadSize), new FixedBufferStream(PayloadSize)];
        _tee = new TeeStream(new TeeStreamOptions(leaveOpen: true), _teeDestinations);
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _tee?.Dispose();
        DisposeAll(_teeDestinations);
        DisposeAll(_manualDestinations);
        GC.SuppressFinalize(this);
    }

    [Benchmark(Baseline = true)]
    public void ManualSequentialLoop()
    {
        ReadOnlySpan<byte> payload = _payload;
        for (int index = 0; index < _manualDestinations.Length; index++)
        {
            _manualDestinations[index].Write(payload);
        }
    }

    [Benchmark]
    public void TeeStreamSequential()
    {
        _tee.Write(_payload);
    }

    private static void DisposeAll(Stream[]? destinations)
    {
        if (destinations is null)
        {
            return;
        }

        foreach (Stream destination in destinations)
        {
            destination.Dispose();
        }
    }

    private sealed class FixedBufferStream : Stream
    {
        private readonly byte[] _buffer;
        private volatile int _checksum;

        public FixedBufferStream(int capacity) => _buffer = GC.AllocateUninitializedArray<byte>(capacity);

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        [MethodImpl(MethodImplOptions.NoInlining)]
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            buffer.CopyTo(_buffer);
            _checksum = buffer.Length == 0 ? 0 : _buffer[0] ^ _buffer[^1] ^ buffer.Length;
        }
    }
}
