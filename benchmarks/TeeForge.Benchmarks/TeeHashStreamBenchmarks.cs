using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TeeForge.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 3, iterationCount: 8)]
public class TeeHashStreamBenchmarks : IDisposable
{
    private TeeBufferedStream _buffered = null!;
    private ObservableSinkStream _bufferedDestination = null!;
    private byte[] _payload = null!;
    private TeeHashStream _sha256 = null!;
    private ObservableSinkStream _sha256Destination = null!;
    private TeeHashResults _sha256Results = null!;
    private TeeHashStream _sha256AndXxHash3 = null!;
    private ObservableSinkStream _sha256AndXxHash3Destination = null!;
    private TeeHashResults<TeeHashAlgorithm> _sha256AndXxHash3Results = null!;
    private TeeHashStream _twoHashes = null!;
    private ObservableSinkStream _twoHashesDestination = null!;
    private TeeHashResults _twoHashResults = null!;
    private TeeHashStream _xxHash3 = null!;
    private ObservableSinkStream _xxHash3Destination = null!;
    private TeeHashResults<TeeHashAlgorithm> _xxHash3Results = null!;

    [Params(4 * 1024, 64 * 1024, 1024 * 1024)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _payload = GC.AllocateUninitializedArray<byte>(PayloadSize);
        Random.Shared.NextBytes(_payload);
        var options = new TeeBufferedStreamOptions(leaveOpen: true);

        _bufferedDestination = new ObservableSinkStream();
        _buffered = new TeeBufferedStream([_bufferedDestination], options);

        _sha256Destination = new ObservableSinkStream();
        _sha256 = new TeeHashStream(
            [HashAlgorithmName.SHA256],
            out _sha256Results,
            [_sha256Destination],
            options);

        _twoHashesDestination = new ObservableSinkStream();
        _twoHashes = new TeeHashStream(
            [HashAlgorithmName.SHA256, HashAlgorithmName.SHA512],
            out _twoHashResults,
            [_twoHashesDestination],
            options);

        _xxHash3Destination = new ObservableSinkStream();
        _xxHash3 = new TeeHashStream(
            [TeeHashAlgorithm.XxHash3],
            out _xxHash3Results,
            [_xxHash3Destination],
            options);

        _sha256AndXxHash3Destination = new ObservableSinkStream();
        _sha256AndXxHash3 = new TeeHashStream(
            [TeeHashAlgorithm.SHA256, TeeHashAlgorithm.XxHash3],
            out _sha256AndXxHash3Results,
            [_sha256AndXxHash3Destination],
            options);
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    [Benchmark(Baseline = true)]
    public void BufferedWithoutHash()
    {
        _buffered.Write(_payload);
        _buffered.Flush();
    }

    [Benchmark]
    public void Sha256()
    {
        _sha256.Write(_payload);
        _sha256.Flush();
    }

    [Benchmark]
    public void Sha256AndSha512()
    {
        _twoHashes.Write(_payload);
        _twoHashes.Flush();
    }

    [Benchmark]
    public void XxHash3()
    {
        _xxHash3.Write(_payload);
        _xxHash3.Flush();
    }

    [Benchmark]
    public void Sha256AndXxHash3()
    {
        _sha256AndXxHash3.Write(_payload);
        _sha256AndXxHash3.Flush();
    }

    public void Dispose()
    {
        _buffered?.Dispose();
        _sha256?.Dispose();
        _twoHashes?.Dispose();
        _xxHash3?.Dispose();
        _sha256AndXxHash3?.Dispose();
        _bufferedDestination?.Dispose();
        _sha256Destination?.Dispose();
        _twoHashesDestination?.Dispose();
        _xxHash3Destination?.Dispose();
        _sha256AndXxHash3Destination?.Dispose();
        GC.KeepAlive(_sha256Results);
        GC.KeepAlive(_twoHashResults);
        GC.KeepAlive(_xxHash3Results);
        GC.KeepAlive(_sha256AndXxHash3Results);
        GC.SuppressFinalize(this);
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
