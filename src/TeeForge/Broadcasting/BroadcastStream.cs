using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using TeeForge.Broadcasting.Internal;

namespace TeeForge.Broadcasting;

/// <summary>Broadcasts one readable source through a shared buffer to independent reader streams.</summary>
/// <remarks>
/// This object owns the broadcast; use <see cref="Readers"/> for Stream endpoints.
/// A background asynchronous pump starts at construction, at the source's current position.
/// Readers must progress concurrently or be disposed to release backpressure. The caller must
/// not otherwise operate on the source until the pump stops. No endpoint supports seeking.
/// </remarks>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "BroadcastStream names the owner of a fixed set of stream endpoints.")]
public class BroadcastStream : IDisposable, IAsyncDisposable
{
    private readonly Stream _source;
    private readonly BroadcastStreamOptions _options;
    private readonly BroadcastPipe _pipe;
    private readonly IBroadcastObserver? _observer;
    private readonly CancellationTokenSource _stop;
    private readonly Task _completion;
    private readonly object _disposeLock = new();
    private Task? _disposeTask;
    private ExceptionDispatchInfo? _failure;
    private long _bytesBroadcast;
    private int _activeReaders;

    /// <summary>Starts a buffered broadcast with a fixed positive number of readers.</summary>
    /// <param name="source">The readable source, consumed from its current position.</param>
    /// <param name="readerCount">The number of independent readers.</param>
    /// <param name="options">The buffer and ownership options, or null for defaults.</param>
    /// <param name="cancellationToken">Cancellation of the entire source pump.</param>
    public BroadcastStream(
        Stream source,
        int readerCount,
        BroadcastStreamOptions? options = null,
        CancellationToken cancellationToken = default)
        : this(source, readerCount, options, observer: null, cancellationToken)
    {
    }

    internal BroadcastStream(
        Stream source,
        int readerCount,
        BroadcastStreamOptions? options,
        IBroadcastObserver? observer,
        CancellationToken cancellationToken)
    {
        ValidateSource(source, readerCount);
        _source = source;
        _options = options ?? BroadcastStreamOptions.Default;
        _observer = observer;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeReaders = readerCount;
        _pipe = new BroadcastPipe(readerCount, new BroadcastPipeOptions(
            pauseWriterThreshold: _options.PauseWriterThreshold,
            resumeWriterThreshold: _options.ResumeWriterThreshold,
            minimumSegmentSize: _options.BufferSize,
            useSynchronizationContext: false));
        Readers = Array.AsReadOnly<Stream>([.. _pipe.Readers.Select(reader => new BroadcastReaderStream(this, reader))]);
        // No virtual calls or derived-instance state are accessed by the pump.
        _completion = Task.Run(PumpAsync, CancellationToken.None);
    }

    /// <summary>Gets the stable, read-only list of independently positioned reader streams.</summary>
    public IReadOnlyList<Stream> Readers { get; }

    /// <summary>
    /// Gets the pump task. Success means source EOF; it does not mean all readers have consumed the buffer.
    /// Source failures fault this task. Cancellation, disposal, or losing every reader before EOF cancels it.
    /// </summary>
    public Task Completion => _completion;

    /// <summary>Gets the number of bytes admitted to the broadcast, independently of reader progress.</summary>
    public long BytesBroadcast => Interlocked.Read(ref _bytesBroadcast);

    /// <summary>Stops the pump, closes readers, and disposes the source unless configured to leave it open.</summary>
    /// <remarks>Pump failures are reported by Completion and reader endpoints; disposal reports cleanup failures.</remarks>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the broadcast's managed resources.</summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            BeginDispose().GetAwaiter().GetResult();
        }
    }

    /// <summary>Asynchronously stops the pump and releases its readers and owned source.</summary>
    public virtual async ValueTask DisposeAsync()
    {
        await BeginDispose().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    internal static void ValidateSource(Stream source, int readerCount)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(readerCount);
        if (!source.CanRead)
        {
            throw new ArgumentException("The broadcast source must be readable.", nameof(source));
        }
    }

    internal void ThrowTerminalFailure() => Volatile.Read(ref _failure)?.Throw();

    internal void ReaderClosed()
    {
        if (Interlocked.Decrement(ref _activeReaders) == 0)
        {
            _stop.Cancel();
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            try
            {
                await PumpCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _observer?.Dispose();
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _failure, ExceptionDispatchInfo.Capture(exception));
            throw;
        }
        finally
        {
            // Readers drain committed bytes before their endpoint reports the terminal failure.
            _pipe.Writer.Complete();
        }
    }

    private async Task PumpCoreAsync()
    {
        CancellationToken cancellationToken = _stop.Token;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Memory<byte> buffer = _pipe.Writer.GetMemory(_options.BufferSize)[.._options.BufferSize];
            int count = await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (count == 0)
            {
                _observer?.Complete();
                return;
            }

            _observer?.Append(buffer.Span[..count]);
            _pipe.Writer.Advance(count);
            Interlocked.Add(ref _bytesBroadcast, count);
            System.IO.Pipelines.FlushResult flush = await _pipe.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (flush.IsCanceled || flush.IsCompleted)
            {
                throw new OperationCanceledException("The broadcast stopped before source EOF.", cancellationToken);
            }
        }
    }

    private Task BeginDispose()
    {
        lock (_disposeLock)
        {
            return _disposeTask ??= DisposeCoreAsync();
        }
    }

    private async Task DisposeCoreAsync()
    {
        List<Exception>? failures = null;
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        await _completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        foreach (Stream reader in Readers)
        {
            try
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        try
        {
            if (!_options.LeaveOpen)
            {
                await _source.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }
        finally
        {
            _stop.Dispose();
        }

        if (failures is { Count: 1 })
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures is not null)
        {
            throw new AggregateException("Broadcast disposal failed.", failures);
        }
    }
}
