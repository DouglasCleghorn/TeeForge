using System.Threading.Channels;
using TeeForge.Networking.Internal;

namespace TeeForge.Networking;

/// <summary>Recombines dynamically managed data paths into one readable logical byte stream.</summary>
/// <remarks>
/// One read may run at a time. Paths may be added while a read waits for connectivity. The receiver
/// owns supplied paths unless its options leave them open.
/// </remarks>
public class MultipathReceiverStream : Stream
{
    private readonly object _stateLock = new();
    private readonly Dictionary<Guid, MultipathReceiverPath> _paths = [];
    private readonly SortedDictionary<ulong, MultipathReceiveGroup> _groups = [];
    private readonly Channel<ReceiverEvent> _events = Channel.CreateUnbounded<ReceiverEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly MultipathStreamOptions _options;
    private readonly SemaphoreSlim _addGate = new(1, 1);
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private Guid? _sessionId;
    private ulong _nextSequence;
    private ulong? _finalSequence;
    private byte[]? _output;
    private int _outputOffset;
    private bool _raid0Observed;
    private Exception? _fault;
    private int _disposeState;

    /// <summary>Initializes a receiver that binds to the first added path's session.</summary>
    public MultipathReceiverStream()
        : this(new MultipathStreamOptions())
    {
    }

    /// <summary>Initializes a receiver with explicit options that binds to the first path's session.</summary>
    public MultipathReceiverStream(MultipathStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Initializes a receiver that accepts only paths for the specified session and options.</summary>
    public MultipathReceiverStream(Guid expectedSessionId, MultipathStreamOptions options)
        : this(options)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(expectedSessionId, Guid.Empty);
        _sessionId = expectedSessionId;
    }

    /// <summary>Gets the bound session identifier, or <see langword="null"/> before the first path joins.</summary>
    public Guid? SessionId
    {
        get
        {
            lock (_stateLock)
            {
                return _sessionId;
            }
        }
    }

    /// <summary>Gets the number of active receiver paths.</summary>
    public int PathCount
    {
        get
        {
            lock (_stateLock)
            {
                return _paths.Count;
            }
        }
    }

    /// <inheritdoc/>
    public override bool CanRead => Volatile.Read(ref _disposeState) == 0 && Volatile.Read(ref _fault) is null;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Reads, initializes, and adds one path, returning the sender-assigned path identifier.</summary>
    public ValueTask<Guid> AddPathAsync(
        Stream path,
        CancellationToken cancellationToken = default) =>
        AddPathCoreAsync(path, initializer: null, cancellationToken);

    /// <summary>Initializes and adds one path, returning the sender-assigned path identifier.</summary>
    public ValueTask<Guid> AddPathAsync(
        Stream path,
        Func<Stream, CancellationToken, ValueTask> initializer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        return AddPathCoreAsync(path, initializer, cancellationToken);
    }

    /// <summary>Stops receiving a path without changing the interpretation of in-flight groups.</summary>
    public async ValueTask<bool> RemovePathAsync(Guid pathId)
    {
        ThrowIfDisposed();
        MultipathReceiverPath? removed;
        lock (_stateLock)
        {
            if (!_paths.Remove(pathId, out removed))
            {
                return false;
            }
        }

        await removed.StopAsync().ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        byte[] temporary = new byte[buffer.Length];
        int read = ReadAsync(temporary, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        temporary.AsSpan(0, read).CopyTo(buffer);
        return read;
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ThrowIfFaulted();
            if (buffer.IsEmpty)
            {
                return 0;
            }

            while (true)
            {
                if (_output is not null)
                {
                    int copyCount = Math.Min(buffer.Length, _output.Length - _outputOffset);
                    _output.AsMemory(_outputOffset, copyCount).CopyTo(buffer);
                    _outputOffset += copyCount;
                    if (_outputOffset == _output.Length)
                    {
                        _output = null;
                        _outputOffset = 0;
                        _groups.Remove(_nextSequence);
                        _nextSequence = checked(_nextSequence + 1);
                    }

                    return copyCount;
                }

                if (TryPrepareOutput())
                {
                    continue;
                }

                if (_finalSequence == _nextSequence)
                {
                    return 0;
                }

                ReceiverEvent nextEvent = await ReadNextEventAsync(cancellationToken).ConfigureAwait(false);
                ProcessEvent(nextEvent);
                ThrowIfFaulted();
            }
        }
        finally
        {
            _readGate.Release();
        }
    }

    /// <inheritdoc/>
    public override void Flush()
    {
        ThrowIfDisposed();
    }

    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            DisposePathsAsync().AsTask().GetAwaiter().GetResult();
            _addGate.Dispose();
            _readGate.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            await DisposePathsAsync().ConfigureAwait(false);
            _addGate.Dispose();
            _readGate.Dispose();
        }

        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async ValueTask<Guid> AddPathCoreAsync(
        Stream path,
        Func<Stream, CancellationToken, ValueTask>? initializer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.CanRead)
        {
            throw new ArgumentException("A receiver path must be readable.", nameof(path));
        }

        ThrowIfDisposed();
        await _addGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (initializer is not null)
            {
                await initializer(path, cancellationToken).ConfigureAwait(false);
            }

            MultipathHello hello = await MultipathProtocol.ReadHelloAsync(path, cancellationToken)
                .ConfigureAwait(false);
            MultipathReceiverPath state;
            lock (_stateLock)
            {
                if (_sessionId is null)
                {
                    _sessionId = hello.SessionId;
                }
                else if (_sessionId != hello.SessionId)
                {
                    throw new InvalidDataException("The path belongs to another multipath session.");
                }

                if (_paths.ContainsKey(hello.PathId))
                {
                    throw new InvalidDataException("The path identifier is already active in this session.");
                }

                state = new MultipathReceiverPath(hello.PathId, path, _options.LeaveOpen);
                _paths.Add(hello.PathId, state);
            }

            _ = PumpPathAsync(state, hello.SessionId);
            return hello.PathId;
        }
        finally
        {
            _addGate.Release();
        }
    }

    private async Task PumpPathAsync(MultipathReceiverPath path, Guid sessionId)
    {
        try
        {
            while (true)
            {
                MultipathReceivedFrame frame = await MultipathProtocol.ReadDataOrCompleteAsync(
                    path.Stream,
                    sessionId,
                    path.PathId,
                    path.StopToken).ConfigureAwait(false);
                if (frame.IsRetired)
                {
                    break;
                }

                await _events.Writer.WriteAsync(
                    ReceiverEvent.CreateFrame(frame),
                    path.StopToken).ConfigureAwait(false);
                if (frame.FinalSequence is not null)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (path.StopToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _events.Writer.TryWrite(ReceiverEvent.CreateFailure(path.PathId, exception));
        }
        finally
        {
            lock (_stateLock)
            {
                _paths.Remove(path.PathId);
            }

            await path.StopAsync().ConfigureAwait(false);
        }
    }

    private bool TryPrepareOutput()
    {
        if (_groups.TryGetValue(_nextSequence, out MultipathReceiveGroup? group) && group.IsDecodable)
        {
            _output = group.Decode();
            _outputOffset = 0;
            return true;
        }

        return false;
    }

    private void ProcessEvent(ReceiverEvent receiverEvent)
    {
        if (receiverEvent.Failure is not null)
        {
            if (_raid0Observed)
            {
                Fault(new IOException(
                    "A RAID-0 data path failed before logical completion.",
                    receiverEvent.Failure));
            }

            return;
        }

        MultipathReceivedFrame frame = receiverEvent.Frame!;
        if (frame.FinalSequence is ulong finalSequence)
        {
            if (finalSequence < _nextSequence ||
                (_finalSequence is not null && _finalSequence != finalSequence))
            {
                Fault(new InvalidDataException("Data paths reported inconsistent completion sequences."));
                return;
            }

            _finalSequence = finalSequence;
            if (_groups.Keys.Any(sequence => sequence >= finalSequence))
            {
                Fault(new InvalidDataException("A data path sent groups at or beyond the completion sequence."));
            }

            return;
        }

        if (frame.Sequence < _nextSequence)
        {
            return;
        }

        ulong distance = frame.Sequence - _nextSequence;
        if (_finalSequence is ulong knownFinalSequence && frame.Sequence >= knownFinalSequence)
        {
            Fault(new InvalidDataException("A data path sent a group at or beyond the completion sequence."));
            return;
        }

        if (distance >= (ulong)_options.MaximumReorderGroups)
        {
            Fault(new InvalidDataException("A data frame exceeds the configured reorder window."));
            return;
        }

        try
        {
            _raid0Observed |= frame.Mode == MultipathStreamMode.Raid0;
            if (_groups.TryGetValue(frame.Sequence, out MultipathReceiveGroup? group))
            {
                group.Add(frame);
            }
            else
            {
                _groups.Add(frame.Sequence, new MultipathReceiveGroup(frame));
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            Fault(exception);
        }
    }

    private async ValueTask<ReceiverEvent> ReadNextEventAsync(CancellationToken cancellationToken)
    {
        bool noPaths;
        lock (_stateLock)
        {
            noPaths = _paths.Count == 0;
        }

        try
        {
            ValueTask<ReceiverEvent> read = _events.Reader.ReadAsync(cancellationToken);
            if (!noPaths || _options.PathAvailabilityTimeout == Timeout.InfiniteTimeSpan)
            {
                return await read.ConfigureAwait(false);
            }

            return await read.AsTask().WaitAsync(_options.PathAvailabilityTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new IOException("No multipath data path became available before the configured timeout.", exception);
        }
    }

    private async ValueTask DisposePathsAsync()
    {
        MultipathReceiverPath[] paths;
        lock (_stateLock)
        {
            paths = _paths.Values.ToArray();
            _paths.Clear();
        }

        foreach (MultipathReceiverPath path in paths)
        {
            await path.StopAsync().ConfigureAwait(false);
        }
    }

    private void Fault(Exception exception) => Interlocked.CompareExchange(ref _fault, exception, null);

    private void ThrowIfFaulted()
    {
        Exception? fault = Volatile.Read(ref _fault);
        if (fault is not null)
        {
            throw new IOException("The multipath receiver is faulted.", fault);
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);

    private sealed class ReceiverEvent
    {
        private ReceiverEvent(MultipathReceivedFrame? frame, Guid failedPathId, Exception? failure)
        {
            Frame = frame;
            FailedPathId = failedPathId;
            Failure = failure;
        }

        internal MultipathReceivedFrame? Frame { get; }

        internal Guid FailedPathId { get; }

        internal Exception? Failure { get; }

        internal static ReceiverEvent CreateFrame(MultipathReceivedFrame frame) =>
            new(frame, Guid.Empty, null);

        internal static ReceiverEvent CreateFailure(Guid pathId, Exception failure) =>
            new(null, pathId, failure);
    }
}
