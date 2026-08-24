using System.Buffers;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using TeeForge.RandomAccess;
using TeeForge.RandomAccess.Internal;

namespace TeeForge.Mirroring;

/// <summary>Presents one or more destination streams as a checked RAID-1-like mirror.</summary>
/// <remarks>
/// Separate operations are not serialized. Callers must apply the same ownership discipline
/// they would apply to an ordinary <see cref="Stream"/>.
/// </remarks>
public class TeeStream : Stream, ITeeRandomAccessStream, ITeeRangeReadSource
{
    private readonly Stream[] _destinations;
    private readonly ITeeRandomAccessStream?[] _randomAccessDestinations;
    private readonly TeeStreamOptions _options;
    private TeeStreamConsistencyException? _fault;
    private int _disposed;

    /// <summary>Initializes a TeeStream that owns the supplied destinations.</summary>
    /// <param name="destinations">The destinations. The first is the primary.</param>
    public TeeStream(params Stream[] destinations)
        : this((IEnumerable<Stream>)destinations, options: null)
    {
    }

    /// <summary>Initializes a TeeStream with explicit options.</summary>
    /// <param name="options">The stream options.</param>
    /// <param name="destinations">The destinations. The first is the primary.</param>
    public TeeStream(TeeStreamOptions options, params Stream[] destinations)
        : this((IEnumerable<Stream>)destinations, options)
    {
    }

    /// <summary>Initializes a TeeStream from an enumerable of destinations.</summary>
    /// <param name="destinations">The destinations. The first is the primary.</param>
    /// <param name="options">The stream options, or <see langword="null"/> for defaults.</param>
    public TeeStream(IEnumerable<Stream> destinations, TeeStreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(destinations);

        _destinations = destinations.ToArray();
        if (_destinations.Length == 0)
        {
            throw new ArgumentException("At least one destination is required.", nameof(destinations));
        }

        var identities = new HashSet<Stream>(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < _destinations.Length; index++)
        {
            Stream destination = _destinations[index]
                ?? throw new ArgumentException($"Destination at index {index} is null.", nameof(destinations));
            if (!identities.Add(destination))
            {
                throw new ArgumentException($"Destination at index {index} is a duplicate object reference.", nameof(destinations));
            }
        }

        _randomAccessDestinations = new ITeeRandomAccessStream?[_destinations.Length];
        for (int index = 0; index < _destinations.Length; index++)
        {
            TeeRandomAccess.TryGet(_destinations[index], out _randomAccessDestinations[index]);
        }

        _options = options ?? TeeStreamOptions.Default;
    }

    /// <inheritdoc/>
    public override bool CanRead => GetCapability("get_CanRead", static destination => destination.CanRead);

    /// <inheritdoc/>
    public override bool CanSeek => GetCapability("get_CanSeek", static destination => destination.CanSeek);

    /// <inheritdoc/>
    public override bool CanTimeout => GetCapability("get_CanTimeout", static destination => destination.CanTimeout);

    /// <inheritdoc/>
    public override bool CanWrite => GetCapability("get_CanWrite", static destination => destination.CanWrite);

    /// <inheritdoc/>
    public bool CanReadAt => !IsDisposed && _randomAccessDestinations.All(static access => access?.CanReadAt == true);

    /// <inheritdoc/>
    public bool CanWriteAt => !IsDisposed && _randomAccessDestinations.All(static access => access?.CanWriteAt == true);

    /// <inheritdoc/>
    public override long Length => InvokeComparableSync("get_Length", static stream => stream.Length);

    /// <inheritdoc/>
    public override long Position
    {
        get => InvokeComparableSync("get_Position", static stream => stream.Position);
        set => InvokeVoidSync("set_Position", stream => stream.Position = value);
    }

    /// <inheritdoc/>
    public override int ReadTimeout
    {
        get => checked((int)InvokeComparableSync("get_ReadTimeout", static stream => stream.ReadTimeout));
        set => InvokeVoidSync("set_ReadTimeout", stream => stream.ReadTimeout = value);
    }

    /// <inheritdoc/>
    public override int WriteTimeout
    {
        get => checked((int)InvokeComparableSync("get_WriteTimeout", static stream => stream.WriteTimeout));
        set => InvokeVoidSync("set_WriteTimeout", stream => stream.WriteTimeout = value);
    }

    /// <inheritdoc/>
    public override void Flush() => InvokeVoidSync("Flush", static stream => stream.Flush());

    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        ThrowIfUnavailable();
        return InvokeVoidAsync("FlushAsync", static (stream, token) => new ValueTask(stream.FlushAsync(token)), cancellationToken);
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
        ThrowIfUnavailable();
        if (buffer.Length == 0)
        {
            return 0;
        }

        int primaryCount;
        ExceptionDispatchInfo? primaryFailure = null;
        try
        {
            primaryCount = _destinations[0].Read(buffer);
        }
        catch (Exception exception)
        {
            primaryCount = 0;
            primaryFailure = ExceptionDispatchInfo.Capture(exception);
        }

        if (primaryFailure is not null)
        {
            Failure[] mirrorFailures = ReadMirrorsAfterPrimaryFailureSync(buffer.Length);
            ThrowFailures("Read", [(0, primaryFailure), .. mirrorFailures.Select(static failure => (failure.Index, failure.Exception))]);
            throw new UnreachableException();
        }

        if (primaryCount == 0)
        {
            return 0;
        }

        ReadOutcome[] outcomes = ReadMirrorsSync(buffer[..primaryCount], primaryCount);
        return FinishRead("Read", buffer[..primaryCount], primaryCount, outcomes);
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnavailable();
        if (buffer.Length == 0)
        {
            return 0;
        }

        int primaryCount;
        ExceptionDispatchInfo? primaryFailure = null;
        try
        {
            primaryCount = await _destinations[0].ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primaryCount = 0;
            primaryFailure = ExceptionDispatchInfo.Capture(exception);
        }

        if (primaryFailure is not null)
        {
            Failure[] mirrorFailures = await ReadMirrorsAfterPrimaryFailureAsync(buffer.Length, cancellationToken).ConfigureAwait(false);
            ThrowFailures("ReadAsync", [(0, primaryFailure), .. mirrorFailures.Select(static failure => (failure.Index, failure.Exception))]);
            throw new UnreachableException();
        }

        if (primaryCount == 0)
        {
            return 0;
        }

        Task<ReadOutcome>[] tasks = new Task<ReadOutcome>[_destinations.Length - 1];
        for (int index = 1; index < _destinations.Length; index++)
        {
            tasks[index - 1] = ReadMirrorAsync(index, primaryCount, cancellationToken);
        }

        ReadOutcome[] outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);
        return FinishRead("ReadAsync", buffer.Span[..primaryCount], primaryCount, outcomes);
    }

    /// <inheritdoc/>
    public int ReadAt(Span<byte> buffer, long offset)
    {
        ThrowIfUnavailable();
        EnsureCanReadAt();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (buffer.Length == 0)
        {
            return 0;
        }

        int primaryCount;
        ExceptionDispatchInfo? primaryFailure = null;
        try
        {
            primaryCount = _randomAccessDestinations[0]!.ReadAt(buffer, offset);
        }
        catch (Exception exception)
        {
            primaryCount = 0;
            primaryFailure = ExceptionDispatchInfo.Capture(exception);
        }

        if (primaryFailure is not null)
        {
            Failure[] mirrorFailures = ReadAtMirrorsAfterPrimaryFailureSync(offset, buffer.Length);
            ThrowFailures("ReadAt", [(0, primaryFailure), .. mirrorFailures.Select(static failure => (failure.Index, failure.Exception))]);
            throw new UnreachableException();
        }

        if (primaryCount == 0)
        {
            return 0;
        }

        ReadOutcome[] outcomes = ReadAtMirrorsSync(buffer[..primaryCount], offset, primaryCount);
        return FinishRead("ReadAt", buffer[..primaryCount], primaryCount, outcomes);
    }

    /// <inheritdoc/>
    public async ValueTask<int> ReadAtAsync(
        Memory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnavailable();
        EnsureCanReadAt();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (buffer.Length == 0)
        {
            return 0;
        }

        int primaryCount;
        ExceptionDispatchInfo? primaryFailure = null;
        try
        {
            primaryCount = await _randomAccessDestinations[0]!
                .ReadAtAsync(buffer, offset, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primaryCount = 0;
            primaryFailure = ExceptionDispatchInfo.Capture(exception);
        }

        if (primaryFailure is not null)
        {
            Failure[] mirrorFailures = await ReadAtMirrorsAfterPrimaryFailureAsync(
                offset,
                buffer.Length,
                cancellationToken).ConfigureAwait(false);
            ThrowFailures("ReadAtAsync", [(0, primaryFailure), .. mirrorFailures.Select(static failure => (failure.Index, failure.Exception))]);
            throw new UnreachableException();
        }

        if (primaryCount == 0)
        {
            return 0;
        }

        Task<ReadOutcome>[] tasks = new Task<ReadOutcome>[_destinations.Length - 1];
        for (int index = 1; index < _destinations.Length; index++)
        {
            tasks[index - 1] = ReadAtMirrorAsync(index, offset, primaryCount, cancellationToken);
        }

        ReadOutcome[] outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);
        return FinishRead("ReadAtAsync", buffer.Span[..primaryCount], primaryCount, outcomes);
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) =>
        InvokeComparableSync("Seek", stream => stream.Seek(offset, origin));

    /// <inheritdoc/>
    public override void SetLength(long value) => InvokeVoidSync("SetLength", stream => stream.SetLength(value));

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ThrowIfUnavailable();

        if (_options.SynchronousMode == TeeStreamSynchronousMode.Concurrent)
        {
            WriteConcurrent(buffer, offset, count);
            return;
        }

        List<Failure>? failures = null;
        for (int index = 0; index < _destinations.Length; index++)
        {
            try
            {
                _destinations[index].Write(buffer, offset, count);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(new Failure(index, ExceptionDispatchInfo.Capture(exception)));
            }
        }

        ThrowFailures("Write", failures);
    }

    private void WriteConcurrent(byte[] buffer, int offset, int count) =>
        InvokeVoidSyncConcurrent("Write", (stream, index) => stream.Write(buffer, offset, count));

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfUnavailable();
        if (_options.SynchronousMode == TeeStreamSynchronousMode.Concurrent)
        {
            byte[] copy = buffer.ToArray();
            InvokeVoidSyncConcurrent("Write", (stream, index) => stream.Write(copy));
            return;
        }

        List<Failure>? failures = null;
        for (int index = 0; index < _destinations.Length; index++)
        {
            try
            {
                _destinations[index].Write(buffer);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(new Failure(index, ExceptionDispatchInfo.Capture(exception)));
            }
        }

        ThrowFailures("Write", failures);
    }

    /// <inheritdoc/>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc/>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnavailable();
        return new ValueTask(InvokeVoidAsync("WriteAsync", (stream, token) => stream.WriteAsync(buffer, token), cancellationToken));
    }

    /// <inheritdoc/>
    public void WriteAt(ReadOnlySpan<byte> buffer, long offset)
    {
        ThrowIfUnavailable();
        EnsureCanWriteAt();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        if (_options.SynchronousMode == TeeStreamSynchronousMode.Concurrent)
        {
            byte[] copy = buffer.ToArray();
            InvokeVoidSyncConcurrent(
                "WriteAt",
                (_, index) => _randomAccessDestinations[index]!.WriteAt(copy, offset));
            return;
        }

        List<Failure>? failures = null;
        for (int index = 0; index < _randomAccessDestinations.Length; index++)
        {
            try
            {
                _randomAccessDestinations[index]!.WriteAt(buffer, offset);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(new Failure(index, ExceptionDispatchInfo.Capture(exception)));
            }
        }

        ThrowFailures("WriteAt", failures);
    }

    /// <inheritdoc/>
    public ValueTask WriteAtAsync(
        ReadOnlyMemory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnavailable();
        EnsureCanWriteAt();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return new ValueTask(InvokeRandomAccessWriteAsync(buffer, offset, cancellationToken));
    }

    /// <inheritdoc/>
    public async ValueTask<Stream> OpenReadRangeAsync(
        long offset,
        long length,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnavailable();
        EnsureCanReadAt();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        long sourceLength = Length;
        long boundedLength = offset >= sourceLength ? 0 : Math.Min(length, sourceLength - offset);
        Task<Stream>[] tasks = new Task<Stream>[_destinations.Length];
        for (int index = 0; index < tasks.Length; index++)
        {
            tasks[index] = OpenDestinationRangeAsync(index, offset, boundedLength, cancellationToken);
        }

        Stream[] ranges;
        try
        {
            ranges = await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            foreach (Task<Stream> task in tasks)
            {
                if (task.Status == TaskStatus.RanToCompletion)
                {
                    await task.Result.DisposeAsync().ConfigureAwait(false);
                }
            }

            throw;
        }

        return new TeeStream(
            ranges,
            new TeeStreamOptions(
                _options.MismatchBehavior,
                _options.SynchronousMode,
                leaveOpen: false));
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!disposing || Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            base.Dispose(disposing);
            return;
        }

        List<Failure>? failures = null;
        try
        {
            if (!_options.LeaveOpen)
            {
                failures = DisposeSync();
            }
        }
        finally
        {
            base.Dispose(disposing);
        }

        ThrowFailures("Dispose", failures);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await base.DisposeAsync().ConfigureAwait(false);
            return;
        }

        List<Failure>? failures = null;
        try
        {
            if (!_options.LeaveOpen)
            {
                Task<Failure?>[] tasks = new Task<Failure?>[_destinations.Length];
                for (int index = 0; index < _destinations.Length; index++)
                {
                    tasks[index] = DisposeDestinationAsync(index);
                }

                Failure?[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
                failures = [.. results.Where(static result => result.HasValue).Select(static result => result!.Value)];
            }
        }
        finally
        {
            await base.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        ThrowFailures("DisposeAsync", failures);
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private bool GetCapability(string operationName, Func<Stream, bool> operation)
    {
        if (IsDisposed)
        {
            return false;
        }

        ResultOutcome[] outcomes;
        if (_options.SynchronousMode == TeeStreamSynchronousMode.Concurrent)
        {
            Task<ResultOutcome>[] tasks = new Task<ResultOutcome>[_destinations.Length];
            for (int index = 0; index < _destinations.Length; index++)
            {
                int capturedIndex = index;
                tasks[index] = Task.Run(() => InvokeComparable(capturedIndex, stream => operation(stream) ? 1 : 0));
            }

            outcomes = Task.WhenAll(tasks).GetAwaiter().GetResult();
        }
        else
        {
            outcomes = new ResultOutcome[_destinations.Length];
            for (int index = 0; index < _destinations.Length; index++)
            {
                outcomes[index] = InvokeComparable(index, stream => operation(stream) ? 1 : 0);
            }
        }

        List<Failure> failures = [.. outcomes.Where(static outcome => outcome.Exception is not null)
            .Select(static outcome => new Failure(outcome.Index, outcome.Exception!))];
        ThrowFailures(operationName, failures);
        return outcomes.All(static outcome => outcome.Result != 0);
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        TeeStreamConsistencyException? fault = Volatile.Read(ref _fault);
        if (fault is not null)
        {
            throw new InvalidOperationException("TeeStream is faulted because a previous operation produced inconsistent results.", fault);
        }
    }

    private long InvokeComparableSync(string operationName, Func<Stream, long> operation)
    {
        ThrowIfUnavailable();
        ResultOutcome[] outcomes;
        if (_options.SynchronousMode == TeeStreamSynchronousMode.Concurrent)
        {
            Task<ResultOutcome>[] tasks = new Task<ResultOutcome>[_destinations.Length];
            for (int index = 0; index < _destinations.Length; index++)
            {
                int capturedIndex = index;
                tasks[index] = Task.Run(() => InvokeComparable(capturedIndex, operation));
            }

            outcomes = Task.WhenAll(tasks).GetAwaiter().GetResult();
        }
        else
        {
            outcomes = new ResultOutcome[_destinations.Length];
            for (int index = 0; index < _destinations.Length; index++)
            {
                outcomes[index] = InvokeComparable(index, operation);
            }
        }

        List<Failure> failures = [.. outcomes.Where(static outcome => outcome.Exception is not null)
            .Select(static outcome => new Failure(outcome.Index, outcome.Exception!))];

        ResultOutcome primary = outcomes[0];
        if (primary.Exception is not null)
        {
            ThrowFailures(operationName, failures);
        }

        TeeStreamMismatch[] mismatches = [.. outcomes.Skip(1)
            .Where(outcome => outcome.Exception is null && outcome.Result != primary.Result)
            .Select(static outcome => new TeeStreamMismatch(outcome.Index, outcome.Result, null))];

        FinishConsistency(operationName, primary.Result, mismatches, failures);
        return primary.Result;
    }

    private ResultOutcome InvokeComparable(int index, Func<Stream, long> operation)
    {
        try
        {
            return new ResultOutcome(index, operation(_destinations[index]), null);
        }
        catch (Exception exception)
        {
            return new ResultOutcome(index, 0, ExceptionDispatchInfo.Capture(exception));
        }
    }

    private void InvokeVoidSync(string operationName, Action<Stream> operation)
    {
        ThrowIfUnavailable();
        if (_options.SynchronousMode == TeeStreamSynchronousMode.Concurrent)
        {
            InvokeVoidSyncConcurrent(operationName, (stream, index) => operation(stream));
        }
        else
        {
            InvokeVoidSyncSequential(operationName, operation);
        }
    }

    private void InvokeVoidSyncSequential(string operationName, Action<Stream> operation)
    {
        List<Failure>? failures = null;
        for (int index = 0; index < _destinations.Length; index++)
        {
            try
            {
                operation(_destinations[index]);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(new Failure(index, ExceptionDispatchInfo.Capture(exception)));
            }
        }

        ThrowFailures(operationName, failures);
    }

    private void InvokeVoidSyncConcurrent(string operationName, Action<Stream, int> operation)
    {
        Task<Failure?>[] tasks = new Task<Failure?>[_destinations.Length];
        for (int index = 0; index < _destinations.Length; index++)
        {
            int capturedIndex = index;
            tasks[index] = Task.Run(() =>
            {
                try
                {
                    operation(_destinations[capturedIndex], capturedIndex);
                    return (Failure?)null;
                }
                catch (Exception exception)
                {
                    return new Failure(capturedIndex, ExceptionDispatchInfo.Capture(exception));
                }
            });
        }

        Failure?[] results = Task.WhenAll(tasks).GetAwaiter().GetResult();
        List<Failure> failures = [.. results.Where(static result => result.HasValue).Select(static result => result!.Value)];
        ThrowFailures(operationName, failures);
    }

    private async Task InvokeVoidAsync(
        string operationName,
        Func<Stream, CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken)
    {
        Task<Failure?>[] tasks = new Task<Failure?>[_destinations.Length];
        for (int index = 0; index < _destinations.Length; index++)
        {
            tasks[index] = InvokeDestinationAsync(index, operation, cancellationToken);
        }

        Failure?[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        List<Failure> failures = [.. results.Where(static result => result.HasValue).Select(static result => result!.Value)];
        ThrowFailures(operationName, failures);
    }

    private async Task<Failure?> InvokeDestinationAsync(
        int index,
        Func<Stream, CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await operation(_destinations[index], cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return new Failure(index, ExceptionDispatchInfo.Capture(exception));
        }
    }

    private void EnsureCanReadAt()
    {
        if (!CanReadAt)
        {
            throw new NotSupportedException("Every destination must support random-access reads.");
        }
    }

    private void EnsureCanWriteAt()
    {
        if (!CanWriteAt)
        {
            throw new NotSupportedException("Every destination must support random-access writes.");
        }
    }

    private ReadOutcome[] ReadAtMirrorsSync(ReadOnlySpan<byte> primaryData, long offset, int count)
    {
        if (_destinations.Length == 1)
        {
            return [];
        }

        if (_options.SynchronousMode == TeeStreamSynchronousMode.Concurrent)
        {
            byte[] primaryCopy = primaryData.ToArray();
            Task<ReadOutcome>[] tasks = new Task<ReadOutcome>[_destinations.Length - 1];
            for (int index = 1; index < _destinations.Length; index++)
            {
                int capturedIndex = index;
                tasks[index - 1] = Task.Run(() => ReadAtMirrorSync(capturedIndex, primaryCopy, offset, count));
            }

            return Task.WhenAll(tasks).GetAwaiter().GetResult();
        }

        ReadOutcome[] outcomes = new ReadOutcome[_destinations.Length - 1];
        for (int index = 1; index < _destinations.Length; index++)
        {
            outcomes[index - 1] = ReadAtMirrorSync(index, primaryData, offset, count);
        }

        return outcomes;
    }

    private ReadOutcome ReadAtMirrorSync(
        int index,
        ReadOnlySpan<byte> primaryData,
        long offset,
        int count)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            int total = 0;
            while (total < count)
            {
                int read = _randomAccessDestinations[index]!.ReadAt(
                    rented.AsSpan(total, count - total),
                    checked(offset + total));
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            long? difference = total == count ? FindDifference(primaryData, rented.AsSpan(0, count)) : null;
            return new ReadOutcome(index, total, difference, null);
        }
        catch (Exception exception)
        {
            return new ReadOutcome(index, 0, null, ExceptionDispatchInfo.Capture(exception));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private async Task<ReadOutcome> ReadAtMirrorAsync(
        int index,
        long offset,
        int count,
        CancellationToken cancellationToken)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            int total = 0;
            while (total < count)
            {
                int read = await _randomAccessDestinations[index]!
                    .ReadAtAsync(
                        rented.AsMemory(total, count - total),
                        checked(offset + total),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return new ReadOutcome(index, total, null, null, rented);
        }
        catch (Exception exception)
        {
            ArrayPool<byte>.Shared.Return(rented);
            return new ReadOutcome(index, 0, null, ExceptionDispatchInfo.Capture(exception));
        }
    }

    private Failure[] ReadAtMirrorsAfterPrimaryFailureSync(long offset, int requestedCount)
    {
        if (_destinations.Length == 1)
        {
            return [];
        }

        Failure?[] outcomes = new Failure?[_destinations.Length - 1];
        if (_options.SynchronousMode == TeeStreamSynchronousMode.Concurrent)
        {
            Task<Failure?>[] tasks = new Task<Failure?>[_destinations.Length - 1];
            for (int index = 1; index < _destinations.Length; index++)
            {
                int capturedIndex = index;
                tasks[index - 1] = Task.Run(() => ReadAtOnceAfterFailure(capturedIndex, offset, requestedCount));
            }

            outcomes = Task.WhenAll(tasks).GetAwaiter().GetResult();
        }
        else
        {
            for (int index = 1; index < _destinations.Length; index++)
            {
                outcomes[index - 1] = ReadAtOnceAfterFailure(index, offset, requestedCount);
            }
        }

        return [.. outcomes.Where(static failure => failure.HasValue).Select(static failure => failure!.Value)];
    }

    private Failure? ReadAtOnceAfterFailure(int index, long offset, int requestedCount)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(requestedCount);
        try
        {
            _randomAccessDestinations[index]!.ReadAt(rented.AsSpan(0, requestedCount), offset);
            return null;
        }
        catch (Exception exception)
        {
            return new Failure(index, ExceptionDispatchInfo.Capture(exception));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private async Task<Failure[]> ReadAtMirrorsAfterPrimaryFailureAsync(
        long offset,
        int requestedCount,
        CancellationToken cancellationToken)
    {
        Task<Failure?>[] tasks = new Task<Failure?>[_destinations.Length - 1];
        for (int index = 1; index < _destinations.Length; index++)
        {
            int capturedIndex = index;
            tasks[index - 1] = ReadAtOnceAfterFailureAsync(
                capturedIndex,
                offset,
                requestedCount,
                cancellationToken);
        }

        Failure?[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return [.. results.Where(static result => result.HasValue).Select(static result => result!.Value)];
    }

    private async Task<Failure?> ReadAtOnceAfterFailureAsync(
        int index,
        long offset,
        int requestedCount,
        CancellationToken cancellationToken)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(requestedCount);
        try
        {
            await _randomAccessDestinations[index]!
                .ReadAtAsync(rented.AsMemory(0, requestedCount), offset, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return new Failure(index, ExceptionDispatchInfo.Capture(exception));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private async Task InvokeRandomAccessWriteAsync(
        ReadOnlyMemory<byte> buffer,
        long offset,
        CancellationToken cancellationToken)
    {
        Task<Failure?>[] tasks = new Task<Failure?>[_randomAccessDestinations.Length];
        for (int index = 0; index < tasks.Length; index++)
        {
            int capturedIndex = index;
            tasks[index] = WriteDestinationAtAsync(capturedIndex, buffer, offset, cancellationToken);
        }

        Failure?[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        ThrowFailures(
            "WriteAtAsync",
            [.. results.Where(static result => result.HasValue).Select(static result => result!.Value)]);
    }

    private async Task<Failure?> WriteDestinationAtAsync(
        int index,
        ReadOnlyMemory<byte> buffer,
        long offset,
        CancellationToken cancellationToken)
    {
        try
        {
            await _randomAccessDestinations[index]!
                .WriteAtAsync(buffer, offset, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return new Failure(index, ExceptionDispatchInfo.Capture(exception));
        }
    }

    private async Task<Stream> OpenDestinationRangeAsync(
        int index,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        ITeeRangeReadSource? rangeSource = TeeRandomAccess.TryGetRangeReadSource(_destinations[index]);
        if (rangeSource is not null)
        {
            return await rangeSource.OpenReadRangeAsync(offset, length, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new BoundedRandomAccessReadStream(
            _randomAccessDestinations[index]!,
            offset,
            length);
    }

    private ReadOutcome[] ReadMirrorsSync(ReadOnlySpan<byte> primaryData, int count)
    {
        if (_destinations.Length == 1)
        {
            return [];
        }

        if (_options.SynchronousMode == TeeStreamSynchronousMode.Concurrent)
        {
            byte[] primaryCopy = primaryData.ToArray();
            Task<ReadOutcome>[] tasks = new Task<ReadOutcome>[_destinations.Length - 1];
            for (int index = 1; index < _destinations.Length; index++)
            {
                int capturedIndex = index;
                tasks[index - 1] = Task.Run(() => ReadMirrorSync(capturedIndex, primaryCopy, count));
            }

            return Task.WhenAll(tasks).GetAwaiter().GetResult();
        }

        ReadOutcome[] outcomes = new ReadOutcome[_destinations.Length - 1];
        for (int index = 1; index < _destinations.Length; index++)
        {
            outcomes[index - 1] = ReadMirrorSync(index, primaryData, count);
        }

        return outcomes;
    }

    private ReadOutcome ReadMirrorSync(int index, ReadOnlySpan<byte> primaryData, int count)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            int total = 0;
            while (total < count)
            {
                int read = _destinations[index].Read(rented.AsSpan(total, count - total));
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            long? difference = total == count ? FindDifference(primaryData, rented.AsSpan(0, count)) : null;
            return new ReadOutcome(index, total, difference, null);
        }
        catch (Exception exception)
        {
            return new ReadOutcome(index, 0, null, ExceptionDispatchInfo.Capture(exception));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private async Task<ReadOutcome> ReadMirrorAsync(int index, int count, CancellationToken cancellationToken)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            int total = 0;
            while (total < count)
            {
                int read = await _destinations[index]
                    .ReadAsync(rented.AsMemory(total, count - total), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return new ReadOutcome(index, total, null, null, rented);
        }
        catch (Exception exception)
        {
            ArrayPool<byte>.Shared.Return(rented);
            return new ReadOutcome(index, 0, null, ExceptionDispatchInfo.Capture(exception));
        }
    }

    private int FinishRead(string operationName, ReadOnlySpan<byte> primaryData, int primaryCount, ReadOutcome[] outcomes)
    {
        List<Failure> failures = [.. outcomes.Where(static outcome => outcome.Exception is not null)
            .Select(static outcome => new Failure(outcome.Index, outcome.Exception!))];
        var mismatches = new List<TeeStreamMismatch>();

        foreach (ReadOutcome outcome in outcomes)
        {
            try
            {
                if (outcome.Exception is not null)
                {
                    continue;
                }

                long? firstDifference = outcome.FirstDifference;
                if (outcome.Buffer is not null && outcome.Count == primaryCount)
                {
                    firstDifference = FindDifference(primaryData, outcome.Buffer.AsSpan(0, primaryCount));
                }

                if (outcome.Count != primaryCount || firstDifference is not null)
                {
                    mismatches.Add(new TeeStreamMismatch(outcome.Index, outcome.Count, firstDifference));
                }
            }
            finally
            {
                if (outcome.Buffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(outcome.Buffer);
                }
            }
        }

        FinishConsistency(operationName, primaryCount, [.. mismatches], failures);
        return primaryCount;
    }

    private Failure[] ReadMirrorsAfterPrimaryFailureSync(int requestedCount)
    {
        if (_destinations.Length == 1)
        {
            return [];
        }

        if (_options.SynchronousMode == TeeStreamSynchronousMode.Concurrent)
        {
            Task<Failure?>[] tasks = new Task<Failure?>[_destinations.Length - 1];
            for (int index = 1; index < _destinations.Length; index++)
            {
                int capturedIndex = index;
                tasks[index - 1] = Task.Run(() => ReadOnceAfterFailure(capturedIndex, requestedCount));
            }

            Failure?[] results = Task.WhenAll(tasks).GetAwaiter().GetResult();
            return [.. results.Where(static result => result.HasValue).Select(static result => result!.Value)];
        }

        var failures = new List<Failure>();
        for (int index = 1; index < _destinations.Length; index++)
        {
            Failure? failure = ReadOnceAfterFailure(index, requestedCount);
            if (failure.HasValue)
            {
                failures.Add(failure.Value);
            }
        }

        return [.. failures];
    }

    private Failure? ReadOnceAfterFailure(int index, int requestedCount)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(requestedCount);
        try
        {
#pragma warning disable CA2022 // A primary failure intentionally causes one ordinary best-effort read on every mirror.
            _destinations[index].Read(rented, 0, requestedCount);
#pragma warning restore CA2022
            return null;
        }
        catch (Exception exception)
        {
            return new Failure(index, ExceptionDispatchInfo.Capture(exception));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private async Task<Failure[]> ReadMirrorsAfterPrimaryFailureAsync(int requestedCount, CancellationToken cancellationToken)
    {
        Task<Failure?>[] tasks = new Task<Failure?>[_destinations.Length - 1];
        for (int index = 1; index < _destinations.Length; index++)
        {
            tasks[index - 1] = ReadOnceAfterFailureAsync(index, requestedCount, cancellationToken);
        }

        Failure?[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return [.. results.Where(static result => result.HasValue).Select(static result => result!.Value)];
    }

    private async Task<Failure?> ReadOnceAfterFailureAsync(int index, int requestedCount, CancellationToken cancellationToken)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(requestedCount);
        try
        {
            await _destinations[index].ReadAsync(rented.AsMemory(0, requestedCount), cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return new Failure(index, ExceptionDispatchInfo.Capture(exception));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void FinishConsistency(
        string operationName,
        long primaryResult,
        TeeStreamMismatch[] mismatches,
        List<Failure> failures)
    {
        TeeStreamConsistencyException? consistencyException = null;
        if (mismatches.Length > 0 && _options.MismatchBehavior != TeeStreamMismatchBehavior.UsePrimary)
        {
            consistencyException = new TeeStreamConsistencyException(operationName, primaryResult, mismatches);
            if (_options.MismatchBehavior == TeeStreamMismatchBehavior.ThrowAndFault)
            {
                Interlocked.CompareExchange(ref _fault, consistencyException, comparand: null);
            }
        }

        if (consistencyException is null)
        {
            ThrowFailures(operationName, failures);
            return;
        }

        if (failures.Count == 0)
        {
            throw consistencyException;
        }

        int mismatchIndex = mismatches.Min(static mismatch => mismatch.DestinationIndex);
        var ordered = failures
            .Select(static failure => (failure.Index, Exception: failure.Exception.SourceException))
            .Append((Index: mismatchIndex, Exception: (Exception)consistencyException))
            .OrderBy(static item => item.Index)
            .Select(static item => item.Exception);
        throw new AggregateException($"TeeStream operation '{operationName}' failed.", ordered);
    }

    private List<Failure> DisposeSync()
    {
        if (_options.SynchronousMode == TeeStreamSynchronousMode.Concurrent)
        {
            Task<Failure?>[] tasks = new Task<Failure?>[_destinations.Length];
            for (int index = 0; index < _destinations.Length; index++)
            {
                int capturedIndex = index;
                tasks[index] = Task.Run(() => DisposeDestination(capturedIndex));
            }

            Failure?[] results = Task.WhenAll(tasks).GetAwaiter().GetResult();
            return [.. results.Where(static result => result.HasValue).Select(static result => result!.Value)];
        }

        var failures = new List<Failure>();
        for (int index = 0; index < _destinations.Length; index++)
        {
            Failure? failure = DisposeDestination(index);
            if (failure.HasValue)
            {
                failures.Add(failure.Value);
            }
        }

        return failures;
    }

    private Failure? DisposeDestination(int index)
    {
        try
        {
            _destinations[index].Dispose();
            return null;
        }
        catch (Exception exception)
        {
            return new Failure(index, ExceptionDispatchInfo.Capture(exception));
        }
    }

    private async Task<Failure?> DisposeDestinationAsync(int index)
    {
        try
        {
            await _destinations[index].DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return new Failure(index, ExceptionDispatchInfo.Capture(exception));
        }
    }

    private static long? FindDifference(ReadOnlySpan<byte> primary, ReadOnlySpan<byte> mirror)
    {
        int commonLength = Math.Min(primary.Length, mirror.Length);
        int difference = primary[..commonLength].CommonPrefixLength(mirror[..commonLength]);
        return difference == commonLength ? null : difference;
    }

    private static void ThrowFailures(string operationName, IEnumerable<(int Index, ExceptionDispatchInfo Exception)> failures)
    {
        ThrowFailures(operationName, [.. failures.Select(static failure => new Failure(failure.Index, failure.Exception))]);
    }

    private static void ThrowFailures(string operationName, List<Failure>? failures)
    {
        if (failures is null || failures.Count == 0)
        {
            return;
        }

        failures.Sort(static (left, right) => left.Index.CompareTo(right.Index));
        if (failures.All(static failure => failure.Exception.SourceException is OperationCanceledException))
        {
            failures[0].Exception.Throw();
        }

        if (failures.Count == 1)
        {
            failures[0].Exception.Throw();
        }

        throw new AggregateException(
            $"TeeStream operation '{operationName}' failed for {failures.Count} destination(s).",
            failures.Select(static failure => failure.Exception.SourceException));
    }

    private readonly record struct Failure(int Index, ExceptionDispatchInfo Exception);

    private readonly record struct ResultOutcome(int Index, long Result, ExceptionDispatchInfo? Exception);

    private readonly record struct ReadOutcome(
        int Index,
        int Count,
        long? FirstDifference,
        ExceptionDispatchInfo? Exception,
        byte[]? Buffer = null);
}
