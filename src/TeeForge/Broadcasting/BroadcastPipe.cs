// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Adapted for TeeForge from System.IO.Pipelines.Pipe at dotnet/runtime commit
// 4271d88e0aebf3d04f188f1334c2220d80555ef6.

using System.Buffers;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks.Sources;
using TeeForge.Broadcasting.Internal;

namespace TeeForge.Broadcasting;

/// <summary>A one-writer, fixed-many-reader pipe that broadcasts each flushed byte to every reader.</summary>
/// <remarks>
/// Each reader independently owns its consumed and examined cursors. The slowest active reader
/// controls backpressure and shared buffer reclamation.
/// </remarks>
public class BroadcastPipe
{
    private static readonly Action<object?> SignalReaderAwaitable = static state =>
    {
        var readerState = (ReaderState)state!;
        readerState.Owner.ReaderCancellationRequested(readerState);
    };

    private static readonly Action<object?> SignalWriterAwaitable = static state =>
        ((BroadcastPipe)state!).WriterCancellationRequested();

    private static readonly Action<object?> InvokeCompletionCallbacks = static state =>
        ((PipeCompletionCallbacks)state!).Execute();

    private static readonly ContextCallback ExecutionContextRawCallback = ExecuteWithoutExecutionContext!;
    private static readonly SendOrPostCallback SyncContextExecutionContextCallback = ExecuteWithExecutionContext!;
    private static readonly SendOrPostCallback SyncContextExecuteWithoutExecutionContextCallback = ExecuteWithoutExecutionContext!;
    private static readonly Action<object?> ScheduleWithExecutionContextCallback = ExecuteWithExecutionContext!;

    // Mutable struct copied from the Microsoft Pipe segment-pool design.
    private BufferSegmentStack _bufferSegmentPool;

    // The sole BroadcastPipe lock. It protects all shared state transitions.
    private readonly Lock _sync = new();
    private readonly BroadcastPipeOptions _options;
    private readonly ReaderState[] _readerStates;
    private readonly DefaultPipeWriter _writer;
    private readonly ReadOnlyCollection<PipeReader> _readers;

    private TaskCompletionSource<Exception?>[] _readerCompletionSources = [];
    private ReadOnlyCollection<Task<Exception?>> _readerCompletionTasks = Array.AsReadOnly(Array.Empty<Task<Exception?>>());

    private PipeAwaitable _writerAwaitable;
    private PipeCompletion _writerCompletion;
    private PipeCompletion _readerCompletion;
    private PipeOperationState _writerOperationState;

    private BufferSegment? _bufferHead;
    private BufferSegment? _readTail;
    private int _readTailIndex;
    private BufferSegment? _writingHead;
    private Memory<byte> _writingHeadMemory;
    private int _writingHeadBytesBuffered;
    private long _unflushedBytes;

    private int _activeReaderCount;
    private bool _writerCompleted;
    private ExceptionDispatchInfo? _writerException;
    private ExceptionDispatchInfo? _readerSideException;
    private bool _drainBeforeWriterException;
    private bool _disposed;

    /// <summary>Initializes a BroadcastPipe with the default options.</summary>
    /// <param name="readerCount">The fixed positive number of broadcast readers.</param>
    public BroadcastPipe(int readerCount)
        : this(readerCount, BroadcastPipeOptions.Default)
    {
    }

    /// <summary>Initializes a BroadcastPipe with explicit options.</summary>
    /// <param name="readerCount">The fixed positive number of broadcast readers.</param>
    /// <param name="options">The pipe options.</param>
    public BroadcastPipe(int readerCount, BroadcastPipeOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(readerCount);

        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _bufferSegmentPool = new BufferSegmentStack(BroadcastPipeOptions.InitialSegmentPoolSize);
        _writer = new DefaultPipeWriter(this);
        _readerStates = new ReaderState[readerCount];
        var readers = new PipeReader[readerCount];

        for (int index = 0; index < readerCount; index++)
        {
            var state = new ReaderState(this, index);
            _readerStates[index] = state;
            readers[index] = new DefaultPipeReader(this, state);
        }

        _readers = Array.AsReadOnly(readers);
        ResetStateUnsynchronized();
    }

    /// <summary>Gets the single writer endpoint.</summary>
    public PipeWriter Writer => _writer;

    /// <summary>Gets the immutable fixed reader list.</summary>
    public IReadOnlyList<PipeReader> Readers => _readers;

    /// <summary>
    /// Gets immutable completion tasks for the current generation. Each task returns the reader's
    /// completion exception or <see langword="null"/> and never faults.
    /// </summary>
    public IReadOnlyList<Task<Exception?>> ReaderCompletions => _readerCompletionTasks;

    /// <summary>Resets completed endpoints for another generation while preserving endpoint identities.</summary>
    public void Reset()
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                throw new InvalidOperationException("The writer and every reader must complete before BroadcastPipe can be reset.");
            }

            _disposed = false;
            _writerCompletion.Reset();
            _readerCompletion.Reset();
            ResetStateUnsynchronized();
        }
    }

    private void ResetStateUnsynchronized()
    {
        _writerAwaitable = new PipeAwaitable(completed: true, _options.UseSynchronizationContext);
        _writerOperationState.Reset();
        _writerCompleted = false;
        _writerException = null;
        _readerSideException = null;
        _drainBeforeWriterException = false;
        _activeReaderCount = _readerStates.Length;
        _readTailIndex = 0;
        _writingHeadBytesBuffered = 0;
        _unflushedBytes = 0;
        _bufferHead = null;
        _readTail = null;
        _writingHead = null;
        _writingHeadMemory = default;

        var sources = new TaskCompletionSource<Exception?>[_readerStates.Length];
        var tasks = new Task<Exception?>[_readerStates.Length];
        for (int index = 0; index < _readerStates.Length; index++)
        {
            _readerStates[index].Reset(_options.UseSynchronizationContext);
            sources[index] = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            tasks[index] = sources[index].Task;
        }

        _readerCompletionSources = sources;
        _readerCompletionTasks = Array.AsReadOnly(tasks);
    }

    private Memory<byte> GetMemory(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);

        if (_writerCompleted)
        {
            throw new InvalidOperationException("Writing is not allowed after the writer completes.");
        }

        if (!_writerOperationState.IsWritingActive
            || _writingHeadMemory.Length == 0
            || _writingHeadMemory.Length < sizeHint)
        {
            AllocateWriteHeadSynchronized(sizeHint);
        }

        return _writingHeadMemory;
    }

    private Span<byte> GetSpan(int sizeHint) => GetMemory(sizeHint).Span;

    private void AllocateWriteHeadSynchronized(int sizeHint)
    {
        lock (_sync)
        {
            if (_writerCompleted)
            {
                throw new InvalidOperationException("Writing is not allowed after the writer completes.");
            }

            _writerOperationState.BeginWrite();

            if (_writingHead is null)
            {
                BufferSegment segment = AllocateSegment(sizeHint);
                _bufferHead = _readTail = _writingHead = segment;
                _readTailIndex = 0;

                foreach (ReaderState reader in _readerStates)
                {
                    if (reader.Active && reader.Head is null)
                    {
                        reader.Head = segment;
                        reader.HeadIndex = 0;
                        reader.ExaminedPosition = 0;
                    }
                }

                return;
            }

            if (_writingHeadMemory.Length != 0 && _writingHeadMemory.Length >= sizeHint)
            {
                return;
            }

            if (_writingHeadBytesBuffered > 0)
            {
                _writingHead.End += _writingHeadBytesBuffered;
                _writingHeadBytesBuffered = 0;
            }

            if (_writingHead.Length == 0)
            {
                _writingHead.ResetMemory();
                RentMemory(_writingHead, sizeHint);
            }
            else
            {
                BufferSegment segment = AllocateSegment(sizeHint);
                _writingHead.SetNext(segment);
                _writingHead = segment;
            }
        }
    }

    private BufferSegment AllocateSegment(int sizeHint)
    {
        BufferSegment segment = _bufferSegmentPool.TryPop(out BufferSegment? pooled)
            ? pooled
            : new BufferSegment();
        RentMemory(segment, sizeHint);
        return segment;
    }

    private void RentMemory(BufferSegment segment, int sizeHint)
    {
        Debug.Assert(segment.MemoryOwner is null);
        MemoryPool<byte>? pool = null;
        int maxSize = -1;
        if (!_options.IsDefaultSharedMemoryPool)
        {
            pool = _options.Pool;
            maxSize = pool.MaxBufferSize;
        }

        if (sizeHint <= maxSize)
        {
            segment.SetOwnedMemory(pool!.Rent(GetSegmentSize(sizeHint, maxSize)));
        }
        else
        {
            segment.SetOwnedMemory(ArrayPool<byte>.Shared.Rent(GetSegmentSize(sizeHint)));
        }

        _writingHeadMemory = segment.AvailableMemory;
    }

    private int GetSegmentSize(int sizeHint, int maximum = int.MaxValue) =>
        Math.Min(maximum, Math.Max(_options.MinimumSegmentSize, sizeHint));

    private void ReturnSegmentUnsynchronized(BufferSegment segment)
    {
        if (_bufferSegmentPool.Count < BroadcastPipeOptions.MaxSegmentPoolSize)
        {
            _bufferSegmentPool.Push(segment);
        }
    }

    private void Advance(int bytes)
    {
        lock (_sync)
        {
            if (_writerCompleted)
            {
                throw new InvalidOperationException("Writing is not allowed after the writer completes.");
            }

            if ((uint)bytes > (uint)_writingHeadMemory.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(bytes));
            }

            if (_activeReaderCount == 0)
            {
                return;
            }

            AdvanceCore(bytes);
        }
    }

    private void AdvanceCore(int bytes)
    {
        _unflushedBytes += bytes;
        _writingHeadBytesBuffered += bytes;
        _writingHeadMemory = _writingHeadMemory[bytes..];
    }

    private ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<FlushResult>(cancellationToken);
        }

        List<CompletionData>? readerCompletions;
        ValueTask<FlushResult> result;
        lock (_sync)
        {
            if (_writerCompleted)
            {
                throw new InvalidOperationException("Writing is not allowed after the writer completes.");
            }

            PrepareFlushUnsynchronized(cancellationToken, out readerCompletions, out result);
        }

        ScheduleAll(_options.ReaderScheduler, readerCompletions);
        return result;
    }

    private void PrepareFlushUnsynchronized(
        CancellationToken cancellationToken,
        out List<CompletionData>? readerCompletions,
        out ValueTask<FlushResult> result)
    {
        readerCompletions = CommitUnsynchronized();
        _writerAwaitable.BeginOperation(SignalWriterAwaitable, this, cancellationToken);

        if (_writerAwaitable.IsCompleted)
        {
            result = new ValueTask<FlushResult>(GetFlushResultUnsynchronized());
        }
        else
        {
            result = new ValueTask<FlushResult>(_writer, token: 0);
        }
    }

    private List<CompletionData>? CommitUnsynchronized()
    {
        _writerOperationState.EndWrite();
        if (_unflushedBytes == 0)
        {
            if (_activeReaderCount == 0)
            {
                ReclaimSegmentsUnsynchronized();
            }

            return null;
        }

        long oldMaximum = GetMaximumUnexaminedBytesUnsynchronized();
        Debug.Assert(_writingHead is not null);
        _writingHead.End += _writingHeadBytesBuffered;
        _readTail = _writingHead;
        _readTailIndex = _writingHead.End;

        long newMaximum = GetMaximumUnexaminedBytesUnsynchronized();
        if (_options.PauseWriterThreshold > 0
            && oldMaximum < _options.PauseWriterThreshold
            && newMaximum >= _options.PauseWriterThreshold
            && _activeReaderCount > 0)
        {
            _writerAwaitable.SetUncompleted();
        }

        List<CompletionData>? completions = null;
        foreach (ReaderState reader in _readerStates)
        {
            if (!reader.Active || GetAvailableLengthUnsynchronized(reader) < reader.MinimumReadBytes)
            {
                continue;
            }

            reader.Awaitable.Complete(out CompletionData completion);
            AddCompletion(ref completions, completion);
        }

        _unflushedBytes = 0;
        _writingHeadBytesBuffered = 0;
        return completions;
    }

    private ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<FlushResult>(cancellationToken);
        }

        List<CompletionData>? readerCompletions;
        ValueTask<FlushResult> result;
        lock (_sync)
        {
            if (_writerCompleted)
            {
                throw new InvalidOperationException("Writing is not allowed after the writer completes.");
            }

            if (_activeReaderCount == 0)
            {
                return new ValueTask<FlushResult>(new FlushResult(isCanceled: false, isCompleted: true));
            }

            AllocateWriteHeadIfNeededUnsynchronized(0);
            if (source.Length <= _writingHeadMemory.Length)
            {
                source.CopyTo(_writingHeadMemory);
                AdvanceCore(source.Length);
            }
            else
            {
                WriteMultiSegment(source.Span);
            }

            PrepareFlushUnsynchronized(cancellationToken, out readerCompletions, out result);
        }

        ScheduleAll(_options.ReaderScheduler, readerCompletions);
        return result;
    }

    private void AllocateWriteHeadIfNeededUnsynchronized(int sizeHint)
    {
        _writerOperationState.BeginWrite();
        if (_writingHead is null)
        {
            BufferSegment segment = AllocateSegment(sizeHint);
            _bufferHead = _readTail = _writingHead = segment;
            _readTailIndex = 0;
            foreach (ReaderState reader in _readerStates)
            {
                if (reader.Active && reader.Head is null)
                {
                    reader.Head = segment;
                    reader.HeadIndex = 0;
                    reader.ExaminedPosition = 0;
                }
            }
        }
        else if (_writingHeadMemory.Length == 0 || _writingHeadMemory.Length < sizeHint)
        {
            if (_writingHeadBytesBuffered > 0)
            {
                _writingHead.End += _writingHeadBytesBuffered;
                _writingHeadBytesBuffered = 0;
            }

            BufferSegment segment = AllocateSegment(sizeHint);
            _writingHead.SetNext(segment);
            _writingHead = segment;
        }
    }

    private void WriteMultiSegment(ReadOnlySpan<byte> source)
    {
        Span<byte> destination = _writingHeadMemory.Span;
        while (true)
        {
            int writable = Math.Min(destination.Length, source.Length);
            source[..writable].CopyTo(destination);
            source = source[writable..];
            AdvanceCore(writable);
            if (source.Length == 0)
            {
                return;
            }

            Debug.Assert(_writingHead is not null);
            _writingHead.End += _writingHeadBytesBuffered;
            _writingHeadBytesBuffered = 0;
            BufferSegment segment = AllocateSegment(0);
            _writingHead.SetNext(segment);
            _writingHead = segment;
            destination = _writingHeadMemory.Span;
        }
    }

    private void CompleteWriter(Exception? exception)
    {
        List<CompletionData>? readerCompletions = null;
        PipeCompletionCallbacks? callbacks;
        lock (_sync)
        {
            if (_writerCompleted)
            {
                return;
            }

            readerCompletions = CommitUnsynchronized();
            _writerCompleted = true;
            _writerException = exception is null ? null : ExceptionDispatchInfo.Capture(exception);
            _drainBeforeWriterException = false;
            callbacks = _writerCompletion.TryComplete(exception);

            foreach (ReaderState reader in _readerStates)
            {
                if (!reader.Active)
                {
                    continue;
                }

                reader.Awaitable.Complete(out CompletionData completion);
                AddCompletion(ref readerCompletions, completion);
            }

            if (_activeReaderCount == 0)
            {
                CompletePipeUnsynchronized();
            }
        }

        ScheduleCallbacks(_options.ReaderScheduler, callbacks);
        ScheduleAll(_options.ReaderScheduler, readerCompletions);
    }

    private ValueTask<ReadResult> ReadAsync(ReaderState reader, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<ReadResult>(cancellationToken);
        }

        lock (_sync)
        {
            ThrowIfReaderCompleted(reader);
            reader.Awaitable.BeginOperation(SignalReaderAwaitable, reader, cancellationToken);
            if (reader.Awaitable.IsCompleted)
            {
                return new ValueTask<ReadResult>(GetReadResultUnsynchronized(reader));
            }

            return new ValueTask<ReadResult>(reader.Endpoint!, token: 0);
        }
    }

    private ValueTask<ReadResult> ReadAtLeastAsync(
        ReaderState reader,
        int minimumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumBytes);

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<ReadResult>(cancellationToken);
        }

        CompletionData writerCompletion = default;
        ValueTask<ReadResult> result;
        lock (_sync)
        {
            ThrowIfReaderCompleted(reader);
            reader.Awaitable.BeginOperation(SignalReaderAwaitable, reader, cancellationToken);
            if (reader.Awaitable.IsCompleted)
            {
                ReadResult readResult = GetReadResultUnsynchronized(reader);
                if (GetAvailableLengthUnsynchronized(reader) >= minimumBytes
                    || readResult.IsCanceled
                    || readResult.IsCompleted
                    || _writerCompleted)
                {
                    return new ValueTask<ReadResult>(readResult);
                }

                reader.Awaitable.SetUncompleted();
                reader.OperationState.EndRead();
                reader.LastReadHead = null;
                reader.LastReadTail = null;
                reader.Awaitable.BeginOperation(SignalReaderAwaitable, reader, cancellationToken);
            }

            if (!_writerAwaitable.IsCompleted)
            {
                _writerAwaitable.Complete(out writerCompletion);
            }

            reader.MinimumReadBytes = minimumBytes;
            result = new ValueTask<ReadResult>(reader.Endpoint!, token: 0);
        }

        TrySchedule(_options.WriterScheduler, writerCompletion);
        return result;
    }

    private bool TryRead(ReaderState reader, out ReadResult result)
    {
        lock (_sync)
        {
            ThrowIfReaderCompleted(reader);
            if (GetAvailableLengthUnsynchronized(reader) > 0 || reader.Awaitable.IsCompleted || _writerCompleted)
            {
                result = GetReadResultUnsynchronized(reader);
                return true;
            }

            if (reader.Awaitable.IsRunning)
            {
                throw new InvalidOperationException("A read operation is already in progress for this reader.");
            }

            reader.OperationState.BeginReadTentative();
            result = default;
            return false;
        }
    }

    private ReadResult GetReadResultUnsynchronized(ReaderState reader)
    {
        long available = GetAvailableLengthUnsynchronized(reader);
        if (_writerException is not null && (!_drainBeforeWriterException || available == 0))
        {
            _writerException.Throw();
        }

        bool canceled = reader.Awaitable.ObserveCancellation();
        bool completed = _writerCompleted && _writerException is null;
        ReadOnlySequence<byte> buffer = default;
        if (reader.Head is not null && _readTail is not null)
        {
            buffer = new ReadOnlySequence<byte>(reader.Head, reader.HeadIndex, _readTail, _readTailIndex);
        }

        reader.LastReadHead = reader.Head;
        reader.LastReadHeadIndex = reader.HeadIndex;
        reader.LastReadTail = _readTail;
        reader.LastReadTailIndex = _readTailIndex;

        if (canceled)
        {
            reader.OperationState.BeginReadTentative();
        }
        else
        {
            reader.OperationState.BeginRead();
        }

        reader.MinimumReadBytes = 0;
        return new ReadResult(buffer, canceled, completed);
    }

    private void AdvanceReader(ReaderState reader, in SequencePosition consumed, in SequencePosition examined)
    {
        CompletionData writerCompletion = default;
        lock (_sync)
        {
            ThrowIfReaderCompleted(reader);
            ValidatePositionUnsynchronized(reader, consumed, out BufferSegment? consumedSegment, out int consumedIndex, out long consumedAbsolute);
            ValidatePositionUnsynchronized(reader, examined, out BufferSegment? examinedSegment, out int examinedIndex, out long examinedAbsolute);
            if (examinedAbsolute < consumedAbsolute)
            {
                throw new InvalidOperationException("The examined position cannot precede the consumed position.");
            }

            long oldMaximum = GetMaximumUnexaminedBytesUnsynchronized();
            bool examinedEverything = examinedSegment == _readTail && examinedIndex == _readTailIndex;

            reader.ExaminedPosition = examinedAbsolute;
            if (consumedSegment is null)
            {
                reader.Head = null;
                reader.HeadIndex = 0;
            }
            else if (consumedIndex == consumedSegment.Length && consumedSegment.NextSegment is not null
                && !ReferenceEquals(consumedSegment, _readTail))
            {
                reader.Head = consumedSegment.NextSegment;
                reader.HeadIndex = 0;
            }
            else
            {
                reader.Head = consumedSegment;
                reader.HeadIndex = consumedIndex;
            }

            reader.LastReadHead = null;
            reader.LastReadTail = null;

            if (examinedEverything && !_writerCompleted)
            {
                reader.Awaitable.SetUncompleted();
            }

            reader.OperationState.EndRead();
            ReclaimSegmentsUnsynchronized();

            long newMaximum = GetMaximumUnexaminedBytesUnsynchronized();
            if (!_writerAwaitable.IsCompleted
                && oldMaximum >= _options.ResumeWriterThreshold
                && newMaximum < _options.ResumeWriterThreshold)
            {
                _writerAwaitable.Complete(out writerCompletion);
            }
        }

        TrySchedule(_options.WriterScheduler, writerCompletion);
    }

    private void CompleteReader(ReaderState reader, Exception? exception)
    {
        CompletionData writerCompletion = default;
        List<CompletionData>? readerCompletions = null;
        PipeCompletionCallbacks? writerCallbacks = null;
        PipeCompletionCallbacks? readerCallbacks = null;

        lock (_sync)
        {
            if (!reader.Active)
            {
                return;
            }

            long oldMaximum = GetMaximumUnexaminedBytesUnsynchronized();
            if (reader.OperationState.IsReadingActive)
            {
                reader.OperationState.EndRead();
            }

            reader.Active = false;
            reader.Head = null;
            reader.LastReadHead = null;
            reader.LastReadTail = null;
            _activeReaderCount--;
            _readerCompletionSources[reader.Index].TrySetResult(exception);

            if (exception is not null
                && _options.ReaderFailureBehavior == BroadcastPipeReaderFailureBehavior.CompletePipe
                && _readerSideException is null)
            {
                _readerSideException = ExceptionDispatchInfo.Capture(exception);
                _writerException = _readerSideException;
                _drainBeforeWriterException = true;
                _writerCompleted = true;
                _unflushedBytes = 0;
                _writingHeadBytesBuffered = 0;
                _writerOperationState.EndWrite();
                _writerAwaitable.Complete(out writerCompletion);
                writerCallbacks = _writerCompletion.TryComplete(exception);

                foreach (ReaderState other in _readerStates)
                {
                    if (!other.Active)
                    {
                        continue;
                    }

                    other.Awaitable.Complete(out CompletionData completion);
                    AddCompletion(ref readerCompletions, completion);
                }
            }

            ReclaimSegmentsUnsynchronized();
            long newMaximum = GetMaximumUnexaminedBytesUnsynchronized();
            if (!_writerAwaitable.IsCompleted
                && (_activeReaderCount == 0
                    || (oldMaximum >= _options.ResumeWriterThreshold && newMaximum < _options.ResumeWriterThreshold)))
            {
                _writerAwaitable.Complete(out writerCompletion);
            }

            if (_activeReaderCount == 0)
            {
                Exception? aggregateException = _readerSideException?.SourceException;
                readerCallbacks = _readerCompletion.TryComplete(aggregateException);
                if (_writerCompleted)
                {
                    CompletePipeUnsynchronized();
                }
            }
        }

        ScheduleCallbacks(_options.ReaderScheduler, writerCallbacks);
        ScheduleCallbacks(_options.WriterScheduler, readerCallbacks);
        ScheduleAll(_options.ReaderScheduler, readerCompletions);
        TrySchedule(_options.WriterScheduler, writerCompletion);
    }

    private void CancelPendingRead(ReaderState reader)
    {
        CompletionData completion;
        lock (_sync)
        {
            ThrowIfReaderCompleted(reader);
            reader.Awaitable.Cancel(out completion);
        }

        TrySchedule(_options.ReaderScheduler, completion);
    }

    private void CancelPendingFlush()
    {
        CompletionData completion;
        lock (_sync)
        {
            _writerAwaitable.Cancel(out completion);
        }

        TrySchedule(_options.WriterScheduler, completion);
    }

    private void ReaderCancellationRequested(ReaderState reader)
    {
        CompletionData completion;
        lock (_sync)
        {
            reader.Awaitable.CancellationTokenFired(out completion);
        }

        TrySchedule(_options.ReaderScheduler, completion);
    }

    private void WriterCancellationRequested()
    {
        CompletionData completion;
        lock (_sync)
        {
            _writerAwaitable.CancellationTokenFired(out completion);
        }

        TrySchedule(_options.WriterScheduler, completion);
    }

    private ValueTaskSourceStatus GetReadAsyncStatus(ReaderState reader)
    {
        lock (_sync)
        {
            if (!reader.Awaitable.IsCompleted)
            {
                return ValueTaskSourceStatus.Pending;
            }

            return _writerException is not null
                && (!_drainBeforeWriterException || GetAvailableLengthUnsynchronized(reader) == 0)
                    ? ValueTaskSourceStatus.Faulted
                    : ValueTaskSourceStatus.Succeeded;
        }
    }

    private ReadResult GetReadAsyncResult(ReaderState reader)
    {
        CancellationTokenRegistration registration = default;
        CancellationToken cancellationToken = default;
        ReadResult result;
        try
        {
            lock (_sync)
            {
                if (!reader.Awaitable.IsCompleted)
                {
                    throw new InvalidOperationException("The read operation has not completed.");
                }

                registration = reader.Awaitable.ReleaseCancellationTokenRegistration(out cancellationToken);
                result = GetReadResultUnsynchronized(reader);
            }
        }
        finally
        {
            registration.Dispose();
        }

        if (result.IsCanceled)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return result;
    }

    private void OnReadAsyncCompleted(
        ReaderState reader,
        Action<object?> continuation,
        object? state,
        ValueTaskSourceOnCompletedFlags flags)
    {
        CompletionData completion;
        bool doubleCompletion;
        lock (_sync)
        {
            reader.Awaitable.OnCompleted(continuation, state, flags, out completion, out doubleCompletion);
        }

        if (doubleCompletion)
        {
            CompleteWriter(new InvalidOperationException("Concurrent operations on one BroadcastPipe reader are not supported."));
        }

        TrySchedule(_options.ReaderScheduler, completion);
    }

    private ValueTaskSourceStatus GetFlushAsyncStatus()
    {
        lock (_sync)
        {
            if (!_writerAwaitable.IsCompleted)
            {
                return ValueTaskSourceStatus.Pending;
            }

            return _readerSideException is null
                ? ValueTaskSourceStatus.Succeeded
                : ValueTaskSourceStatus.Faulted;
        }
    }

    private FlushResult GetFlushAsyncResult()
    {
        CancellationTokenRegistration registration = default;
        CancellationToken cancellationToken = default;
        try
        {
            lock (_sync)
            {
                if (!_writerAwaitable.IsCompleted)
                {
                    throw new InvalidOperationException("The flush operation has not completed.");
                }

                registration = _writerAwaitable.ReleaseCancellationTokenRegistration(out cancellationToken);
                FlushResult result = GetFlushResultUnsynchronized();
                return result;
            }
        }
        finally
        {
            registration.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private FlushResult GetFlushResultUnsynchronized()
    {
        bool canceled = _writerAwaitable.ObserveCancellation();
        _readerSideException?.Throw();
        return new FlushResult(canceled, _activeReaderCount == 0);
    }

    private void OnFlushAsyncCompleted(
        Action<object?> continuation,
        object? state,
        ValueTaskSourceOnCompletedFlags flags)
    {
        CompletionData completion;
        bool doubleCompletion;
        lock (_sync)
        {
            _writerAwaitable.OnCompleted(continuation, state, flags, out completion, out doubleCompletion);
        }

        if (doubleCompletion)
        {
            foreach (PipeReader reader in _readers)
            {
                reader.Complete(new InvalidOperationException("Concurrent BroadcastPipe writer operations are not supported."));
            }
        }

        TrySchedule(_options.WriterScheduler, completion);
    }

    private void OnWriterCompleted(Action<Exception?, object?> callback, object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        PipeCompletionCallbacks? callbacks;
        lock (_sync)
        {
            callbacks = _writerCompletion.AddCallback(callback, state);
        }

        ScheduleCallbacks(_options.ReaderScheduler, callbacks);
    }

    private void OnReaderCompleted(Action<Exception?, object?> callback, object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        PipeCompletionCallbacks? callbacks;
        lock (_sync)
        {
            callbacks = _readerCompletion.AddCallback(callback, state);
        }

        ScheduleCallbacks(_options.WriterScheduler, callbacks);
    }

    private long GetAvailableLengthUnsynchronized(ReaderState reader)
    {
        if (reader.Head is null || _readTail is null)
        {
            return 0;
        }

        return BufferSegment.GetLength(reader.Head, reader.HeadIndex, _readTail, _readTailIndex);
    }

    private long GetMaximumUnexaminedBytesUnsynchronized()
    {
        if (_activeReaderCount == 0 || _readTail is null)
        {
            return 0;
        }

        long tailPosition = _readTail.RunningIndex + (uint)_readTailIndex;
        long minimumExamined = long.MaxValue;
        foreach (ReaderState reader in _readerStates)
        {
            if (reader.Active)
            {
                minimumExamined = Math.Min(minimumExamined, reader.ExaminedPosition);
            }
        }

        return Math.Max(0, tailPosition - minimumExamined);
    }

    private void ReclaimSegmentsUnsynchronized()
    {
        if (_bufferHead is null)
        {
            return;
        }

        if (_activeReaderCount == 0)
        {
            if (!_writerOperationState.IsWritingActive)
            {
                ReleaseAllSegmentsUnsynchronized();
            }

            return;
        }

        long earliestConsumed = long.MaxValue;
        foreach (ReaderState reader in _readerStates)
        {
            if (!reader.Active)
            {
                continue;
            }

            long position = reader.Head is null
                ? 0
                : reader.Head.RunningIndex + (uint)reader.HeadIndex;
            earliestConsumed = Math.Min(earliestConsumed, position);
        }

        while (_bufferHead is not null)
        {
            BufferSegment segment = _bufferHead;
            long segmentEnd = segment.RunningIndex + segment.Length;
            if (earliestConsumed < segmentEnd)
            {
                break;
            }

            // A reader may hold an empty ReadResult whose positions still reference a fully consumed
            // segment. Payload reclamation is safe, but resetting the segment would invalidate the
            // SequencePosition that remains valid until that reader calls AdvanceTo.
            bool pinned = false;
            foreach (ReaderState reader in _readerStates)
            {
                if (reader.Active && ReferenceEquals(reader.LastReadHead, segment))
                {
                    pinned = true;
                    break;
                }
            }

            if (pinned)
            {
                break;
            }

            if (ReferenceEquals(segment, _writingHead))
            {
                if (_writerOperationState.IsWritingActive || _writingHeadBytesBuffered != 0)
                {
                    break;
                }

                foreach (ReaderState reader in _readerStates)
                {
                    if (reader.Active && ReferenceEquals(reader.Head, segment) && reader.HeadIndex == segment.Length)
                    {
                        reader.Head = null;
                        reader.HeadIndex = 0;
                        reader.ExaminedPosition = 0;
                    }
                }

                _bufferHead = null;
                _readTail = null;
                _readTailIndex = 0;
                _writingHead = null;
                _writingHeadMemory = default;
                segment.Reset();
                ReturnSegmentUnsynchronized(segment);
                break;
            }

            // GetMemory may reserve a successor without publishing bytes in it. Keep readers
            // and the published tail together when retiring their fully consumed predecessor.
            BufferSegment? next = segment.NextSegment;
            foreach (ReaderState reader in _readerStates)
            {
                if (reader.Active && ReferenceEquals(reader.Head, segment))
                {
                    reader.Head = next;
                    reader.HeadIndex = 0;
                }
            }

            if (ReferenceEquals(_readTail, segment))
            {
                _readTail = next;
                _readTailIndex = 0;
            }

            _bufferHead = next;
            segment.Reset();
            ReturnSegmentUnsynchronized(segment);
        }
    }

    private void ReleaseAllSegmentsUnsynchronized()
    {
        BufferSegment? segment = _bufferHead ?? _writingHead;
        while (segment is not null)
        {
            BufferSegment current = segment;
            segment = segment.NextSegment;
            current.Reset();
            ReturnSegmentUnsynchronized(current);
        }

        _bufferHead = null;
        _readTail = null;
        _readTailIndex = 0;
        _writingHead = null;
        _writingHeadMemory = default;
        _writingHeadBytesBuffered = 0;

        foreach (ReaderState reader in _readerStates)
        {
            if (reader.Active)
            {
                reader.Head = null;
                reader.HeadIndex = 0;
                reader.ExaminedPosition = 0;
            }
        }
    }

    private void CompletePipeUnsynchronized()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseAllSegmentsUnsynchronized();
    }

    private static void ValidatePositionUnsynchronized(
        ReaderState reader,
        in SequencePosition position,
        out BufferSegment? segment,
        out int index,
        out long absolute)
    {
        object? positionObject = position.GetObject();
        index = position.GetInteger();
        if (positionObject is null)
        {
            if (reader.LastReadHead is not null || index != 0)
            {
                throw new InvalidOperationException("The supplied position does not belong to the active read buffer.");
            }

            segment = null;
            absolute = 0;
            return;
        }

        if (positionObject is not BufferSegment candidate || index < 0 || index > candidate.Length)
        {
            string objectType = positionObject?.GetType().FullName ?? "null";
            throw new InvalidOperationException(
                $"The supplied position does not belong to the active read buffer (object: {objectType}, index: {index}).");
        }

        bool found = false;
        for (BufferSegment? current = reader.LastReadHead; current is not null; current = current.NextSegment)
        {
            if (ReferenceEquals(current, candidate))
            {
                found = true;
                break;
            }

            if (ReferenceEquals(current, reader.LastReadTail))
            {
                break;
            }
        }

        if (!found)
        {
            throw new InvalidOperationException("The supplied position does not belong to the active read buffer.");
        }

        absolute = candidate.RunningIndex + (uint)index;
        long start = reader.LastReadHead!.RunningIndex + (uint)reader.LastReadHeadIndex;
        long end = reader.LastReadTail!.RunningIndex + (uint)reader.LastReadTailIndex;
        if (absolute < start || absolute > end)
        {
            throw new InvalidOperationException("The supplied position does not belong to the active read buffer.");
        }

        segment = candidate;
    }

    private static void ThrowIfReaderCompleted(ReaderState reader)
    {
        if (!reader.Active)
        {
            throw new InvalidOperationException("Reading is not allowed after this reader completes.");
        }
    }

    private static void AddCompletion(ref List<CompletionData>? completions, in CompletionData completion)
    {
        if (completion.Completion is not null)
        {
            (completions ??= []).Add(completion);
        }
    }

    private static void ScheduleAll(PipeScheduler scheduler, List<CompletionData>? completions)
    {
        if (completions is null)
        {
            return;
        }

        foreach (CompletionData completion in completions)
        {
            TrySchedule(scheduler, completion);
        }
    }

    private static void ScheduleCallbacks(PipeScheduler scheduler, PipeCompletionCallbacks? callbacks)
    {
        if (callbacks is not null)
        {
            scheduler.Schedule(InvokeCompletionCallbacks, callbacks);
        }
    }

    private static void TrySchedule(PipeScheduler scheduler, in CompletionData completionData)
    {
        Action<object?> completion = completionData.Completion;
        if (completion is null)
        {
            return;
        }

        if (completionData.SynchronizationContext is null && completionData.ExecutionContext is null)
        {
            scheduler.Schedule(completion, completionData.CompletionState);
        }
        else
        {
            ScheduleWithContext(scheduler, completionData);
        }
    }

    private static void ScheduleWithContext(PipeScheduler scheduler, in CompletionData completionData)
    {
        if (completionData.SynchronizationContext is null)
        {
            scheduler.Schedule(ScheduleWithExecutionContextCallback, completionData);
        }
        else if (completionData.ExecutionContext is null)
        {
            completionData.SynchronizationContext.Post(SyncContextExecuteWithoutExecutionContextCallback, completionData);
        }
        else
        {
            completionData.SynchronizationContext.Post(SyncContextExecutionContextCallback, completionData);
        }
    }

    private static void ExecuteWithoutExecutionContext(object state)
    {
        var completionData = (CompletionData)state;
        completionData.Completion(completionData.CompletionState);
    }

    private static void ExecuteWithExecutionContext(object state)
    {
        var completionData = (CompletionData)state;
        Debug.Assert(completionData.ExecutionContext is not null);
        ExecutionContext.Run(completionData.ExecutionContext, ExecutionContextRawCallback, state);
    }

    private sealed class ReaderState
    {
        public ReaderState(BroadcastPipe owner, int index)
        {
            Owner = owner;
            Index = index;
        }

        public BroadcastPipe Owner { get; }

        public int Index { get; }

        public DefaultPipeReader? Endpoint { get; set; }

        public bool Active { get; set; }

        public PipeAwaitable Awaitable;

        public PipeOperationState OperationState;

        public BufferSegment? Head { get; set; }

        public int HeadIndex { get; set; }

        public long ExaminedPosition { get; set; }

        public int MinimumReadBytes { get; set; }

        public BufferSegment? LastReadHead { get; set; }

        public int LastReadHeadIndex { get; set; }

        public BufferSegment? LastReadTail { get; set; }

        public int LastReadTailIndex { get; set; }

        public void Reset(bool useSynchronizationContext)
        {
            Active = true;
            Awaitable = new PipeAwaitable(completed: false, useSynchronizationContext);
            OperationState.Reset();
            Head = null;
            HeadIndex = 0;
            ExaminedPosition = 0;
            MinimumReadBytes = 0;
            LastReadHead = null;
            LastReadHeadIndex = 0;
            LastReadTail = null;
            LastReadTailIndex = 0;
        }
    }

    private sealed class DefaultPipeReader : PipeReader, IValueTaskSource<ReadResult>
    {
        private readonly BroadcastPipe _pipe;
        private readonly ReaderState _state;

        public DefaultPipeReader(BroadcastPipe pipe, ReaderState state)
        {
            _pipe = pipe;
            _state = state;
            state.Endpoint = this;
        }

        public override bool TryRead(out ReadResult result) => _pipe.TryRead(_state, out result);

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default) =>
            _pipe.ReadAsync(_state, cancellationToken);

        protected override ValueTask<ReadResult> ReadAtLeastAsyncCore(int minimumBytes, CancellationToken cancellationToken) =>
            _pipe.ReadAtLeastAsync(_state, minimumBytes, cancellationToken);

        public override void AdvanceTo(SequencePosition consumed) => _pipe.AdvanceReader(_state, consumed, consumed);

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) =>
            _pipe.AdvanceReader(_state, consumed, examined);

        public override void CancelPendingRead() => _pipe.CancelPendingRead(_state);

        public override void Complete(Exception? exception = null) => _pipe.CompleteReader(_state, exception);

#pragma warning disable CS0672
        public override void OnWriterCompleted(Action<Exception?, object?> callback, object? state) =>
            _pipe.OnWriterCompleted(callback, state);
#pragma warning restore CS0672

        public ValueTaskSourceStatus GetStatus(short token) => _pipe.GetReadAsyncStatus(_state);

        public ReadResult GetResult(short token) => _pipe.GetReadAsyncResult(_state);

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags) =>
            _pipe.OnReadAsyncCompleted(_state, continuation, state, flags);
    }

    private sealed class DefaultPipeWriter : PipeWriter, IValueTaskSource<FlushResult>
    {
        private readonly BroadcastPipe _pipe;

        public DefaultPipeWriter(BroadcastPipe pipe) => _pipe = pipe;

        public override void Complete(Exception? exception = null) => _pipe.CompleteWriter(exception);

        public override void CancelPendingFlush() => _pipe.CancelPendingFlush();

        public override bool CanGetUnflushedBytes => true;

        public override long UnflushedBytes => _pipe._unflushedBytes;

#pragma warning disable CS0672
        public override void OnReaderCompleted(Action<Exception?, object?> callback, object? state) =>
            _pipe.OnReaderCompleted(callback, state);
#pragma warning restore CS0672

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) =>
            _pipe.FlushAsync(cancellationToken);

        public override void Advance(int bytes) => _pipe.Advance(bytes);

        public override Memory<byte> GetMemory(int sizeHint = 0) => _pipe.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0) => _pipe.GetSpan(sizeHint);

        public override ValueTask<FlushResult> WriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default) =>
            _pipe.WriteAsync(source, cancellationToken);

        public ValueTaskSourceStatus GetStatus(short token) => _pipe.GetFlushAsyncStatus();

        public FlushResult GetResult(short token) => _pipe.GetFlushAsyncResult();

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags) =>
            _pipe.OnFlushAsyncCompleted(continuation, state, flags);
    }
}
