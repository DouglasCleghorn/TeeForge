using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TeeForge.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 3, iterationCount: 8)]
public class TeeBufferedStreamBenchmarks : IDisposable
{
    private const int PayloadSize = 64 * 1024;

    private TeeBufferedStream _buffered = null!;
    private Stream[] _bufferedDestinations = null!;
    private byte[] _payload = null!;
    private TeeStream _unbuffered = null!;
    private Stream[] _unbufferedDestinations = null!;

    [Params(64, 256, 1024, 4096, 16 * 1024)]
    public int WriteSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _payload = GC.AllocateUninitializedArray<byte>(PayloadSize);
        Random.Shared.NextBytes(_payload);

        _unbufferedDestinations = [new ObservableSinkStream(), new ObservableSinkStream()];
        _bufferedDestinations = [new ObservableSinkStream(), new ObservableSinkStream()];
        var options = new TeeStreamOptions(leaveOpen: true);
        _unbuffered = new TeeStream(_unbufferedDestinations, options);
        _buffered = new TeeBufferedStream(
            _bufferedDestinations,
            new TeeBufferedStreamOptions(leaveOpen: true));
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    [Benchmark(Baseline = true)]
    public void TeeStream()
    {
        WritePayload(_unbuffered);
        _unbuffered.Flush();
    }

    [Benchmark]
    public void TeeBufferedStream()
    {
        WritePayload(_buffered);
        _buffered.Flush();
    }

    public void Dispose()
    {
        _buffered?.Dispose();
        _unbuffered?.Dispose();
        DisposeAll(_bufferedDestinations);
        DisposeAll(_unbufferedDestinations);
        GC.SuppressFinalize(this);
    }

    private void WritePayload(Stream stream)
    {
        for (int offset = 0; offset < _payload.Length; offset += WriteSize)
        {
            int count = Math.Min(WriteSize, _payload.Length - offset);
            stream.Write(_payload.AsSpan(offset, count));
        }
    }

    private static void DisposeAll(Stream[]? streams)
    {
        if (streams is null)
        {
            return;
        }

        foreach (Stream stream in streams)
        {
            stream.Dispose();
        }
    }

    private sealed class ObservableSinkStream : Stream
    {
        private volatile int _checksum;

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

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        [MethodImpl(MethodImplOptions.NoInlining)]
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _checksum = buffer.Length == 0 ? 0 : buffer[0] ^ buffer[^1] ^ buffer.Length;
        }
    }
}
