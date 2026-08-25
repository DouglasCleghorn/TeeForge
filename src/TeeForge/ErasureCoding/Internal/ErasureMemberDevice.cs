using System.Diagnostics;
using TeeForge.RandomAccess;

namespace TeeForge.ErasureCoding.Internal;

internal enum ErasureMemberDeviceCondition
{
    Online,
    Missing,
    Stale,
    Corrupt,
    Rebuilding,
    Retired,
}

internal enum ErasureMemberOperation
{
    Read,
    Write,
    Flush,
}

internal readonly record struct ErasureMemberPerformanceSnapshot(
    long BytesRead,
    long BytesWritten,
    long ReadOperations,
    long WriteOperations,
    long FlushOperations,
    long ReconstructionBytes,
    long Errors,
    long SampledReads,
    long SampledWrites,
    long SampledFlushes,
    double ReadLatencyMilliseconds,
    double WriteLatencyMilliseconds,
    double FlushLatencyMilliseconds,
    double ReadThroughputBytesPerSecond,
    double WriteThroughputBytesPerSecond,
    double MaximumSampledLatencyMilliseconds,
    long[] LatencyBuckets);

internal sealed class ErasureMemberDevice : IDisposable
{
    private const double EwmaAlpha = 0.125;
    private readonly Stream _stream;
    private readonly ITeeRandomAccessStream? _randomAccess;
    private readonly SemaphoreSlim _positionGate = new(1, 1);
    private readonly object _statisticsLock = new();
    private readonly long[] _latencyBuckets = new long[16];
    private readonly int _latencySampleRate;
    private long _operationSequence;
    private long _bytesRead;
    private long _bytesWritten;
    private long _readOperations;
    private long _writeOperations;
    private long _flushOperations;
    private long _reconstructionBytes;
    private long _errors;
    private long _sampledReads;
    private long _sampledWrites;
    private long _sampledFlushes;
    private double _readLatencyMilliseconds;
    private double _writeLatencyMilliseconds;
    private double _flushLatencyMilliseconds;
    private double _readThroughput;
    private double _writeThroughput;
    private double _maximumLatencyMilliseconds;
    private int _condition = (int)ErasureMemberDeviceCondition.Online;
    private Action? _conditionChanged;

    internal ErasureMemberDevice(
        Stream stream,
        Guid memberId,
        ushort position,
        int latencySampleRate)
    {
        _stream = stream;
        TeeRandomAccess.TryGet(stream, out _randomAccess);
        MemberId = memberId;
        Position = position;
        _latencySampleRate = latencySampleRate;
    }

    internal Stream Stream => _stream;

    internal Guid MemberId { get; }

    internal ushort Position { get; }

    internal ErasureMemberDeviceCondition Condition
    {
        get => (ErasureMemberDeviceCondition)Volatile.Read(ref _condition);
        set
        {
            int previous = Interlocked.Exchange(ref _condition, (int)value);
            if (previous != (int)value)
            {
                Volatile.Read(ref _conditionChanged)?.Invoke();
            }
        }
    }

    internal bool CanRead => Condition == ErasureMemberDeviceCondition.Online && _stream.CanRead;

    internal bool CanWrite => Condition == ErasureMemberDeviceCondition.Online && _stream.CanWrite;

    internal async ValueTask ReadExactlyAtAsync(
        Memory<byte> destination,
        long offset,
        CancellationToken cancellationToken)
    {
        bool sample = ShouldSample();
        long started = Stopwatch.GetTimestamp();
        try
        {
            int total = 0;
            if (_randomAccess?.CanReadAt == true)
            {
                while (total < destination.Length)
                {
                    int read = await _randomAccess.ReadAtAsync(
                        destination[total..],
                        checked(offset + total),
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("An erasure member ended before the requested range was complete.");
                    }

                    total += read;
                }
            }
            else
            {
                await _positionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    _stream.Position = offset;
                    while (total < destination.Length)
                    {
                        int read = await _stream.ReadAsync(destination[total..], cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            throw new EndOfStreamException("An erasure member ended before the requested range was complete.");
                        }

                        total += read;
                    }
                }
                finally
                {
                    _positionGate.Release();
                }
            }

            Interlocked.Add(ref _bytesRead, destination.Length);
            Interlocked.Increment(ref _readOperations);
            RecordSample(ErasureMemberOperation.Read, destination.Length, sample, started);
        }
        catch
        {
            Interlocked.Increment(ref _errors);
            RecordSample(ErasureMemberOperation.Read, destination.Length, force: true, started);
            throw;
        }
    }

    internal async ValueTask WriteAtAsync(
        ReadOnlyMemory<byte> source,
        long offset,
        CancellationToken cancellationToken)
    {
        bool sample = ShouldSample();
        long started = Stopwatch.GetTimestamp();
        try
        {
            if (_randomAccess?.CanWriteAt == true)
            {
                await _randomAccess.WriteAtAsync(source, offset, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _positionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    _stream.Position = offset;
                    await _stream.WriteAsync(source, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _positionGate.Release();
                }
            }

            Interlocked.Add(ref _bytesWritten, source.Length);
            Interlocked.Increment(ref _writeOperations);
            RecordSample(ErasureMemberOperation.Write, source.Length, sample, started);
        }
        catch
        {
            Interlocked.Increment(ref _errors);
            RecordSample(ErasureMemberOperation.Write, source.Length, force: true, started);
            throw;
        }
    }

    internal async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            await _positionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _positionGate.Release();
            }

            Interlocked.Increment(ref _flushOperations);
            RecordSample(ErasureMemberOperation.Flush, 0, force: true, started);
        }
        catch
        {
            Interlocked.Increment(ref _errors);
            RecordSample(ErasureMemberOperation.Flush, 0, force: true, started);
            throw;
        }
    }

    internal void AddReconstructionBytes(int byteCount) =>
        Interlocked.Add(ref _reconstructionBytes, byteCount);

    internal void SetConditionChangedHandler(Action? handler) =>
        Volatile.Write(ref _conditionChanged, handler);

    internal ErasureMemberPerformanceSnapshot GetPerformanceSnapshot()
    {
        lock (_statisticsLock)
        {
            return new ErasureMemberPerformanceSnapshot(
                Interlocked.Read(ref _bytesRead),
                Interlocked.Read(ref _bytesWritten),
                Interlocked.Read(ref _readOperations),
                Interlocked.Read(ref _writeOperations),
                Interlocked.Read(ref _flushOperations),
                Interlocked.Read(ref _reconstructionBytes),
                Interlocked.Read(ref _errors),
                _sampledReads,
                _sampledWrites,
                _sampledFlushes,
                _readLatencyMilliseconds,
                _writeLatencyMilliseconds,
                _flushLatencyMilliseconds,
                _readThroughput,
                _writeThroughput,
                _maximumLatencyMilliseconds,
                (long[])_latencyBuckets.Clone());
        }
    }

    public void Dispose() => _positionGate.Dispose();

    private bool ShouldSample()
    {
        long sequence = Interlocked.Increment(ref _operationSequence);
        return _latencySampleRate != 0 && sequence % _latencySampleRate == 0;
    }

    private void RecordSample(
        ErasureMemberOperation operation,
        int byteCount,
        bool force,
        long started)
    {
        if (!force)
        {
            return;
        }

        double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        double throughput = milliseconds <= 0 ? 0 : byteCount / (milliseconds / 1000);
        int bucket = GetLatencyBucket(milliseconds);
        lock (_statisticsLock)
        {
            _latencyBuckets[bucket]++;
            _maximumLatencyMilliseconds = Math.Max(_maximumLatencyMilliseconds, milliseconds);
            switch (operation)
            {
                case ErasureMemberOperation.Read:
                    _sampledReads++;
                    _readLatencyMilliseconds = UpdateEwma(_readLatencyMilliseconds, milliseconds, _sampledReads);
                    _readThroughput = UpdateEwma(_readThroughput, throughput, _sampledReads);
                    break;
                case ErasureMemberOperation.Write:
                    _sampledWrites++;
                    _writeLatencyMilliseconds = UpdateEwma(_writeLatencyMilliseconds, milliseconds, _sampledWrites);
                    _writeThroughput = UpdateEwma(_writeThroughput, throughput, _sampledWrites);
                    break;
                case ErasureMemberOperation.Flush:
                    _sampledFlushes++;
                    _flushLatencyMilliseconds = UpdateEwma(_flushLatencyMilliseconds, milliseconds, _sampledFlushes);
                    break;
                default:
                    throw new InvalidOperationException("Unknown erasure-member operation.");
            }
        }
    }

    private static double UpdateEwma(double current, double sample, long sampleCount) =>
        sampleCount == 1 ? sample : current + EwmaAlpha * (sample - current);

    private static int GetLatencyBucket(double milliseconds)
    {
        double microseconds = Math.Max(1, milliseconds * 1000);
        int bucket = (int)Math.Log2(microseconds);
        return Math.Min(bucket, 15);
    }
}
