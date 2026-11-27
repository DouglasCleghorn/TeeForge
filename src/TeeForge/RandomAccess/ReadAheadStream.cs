namespace TeeForge.RandomAccess;

/// <summary>Serves seekable reads from one bounded, progressively filled range of a readable source.</summary>
/// <remarks>
/// The source must provide independent range reads and stable contents. The wrapper captures its length
/// and initial position. Reading through this wrapper does not move the source's position.
/// Seeking within the buffered prefix reuses it. Seeking elsewhere opens a new range on the next read.
/// </remarks>
public class ReadAheadStream : Stream, ITeeRandomAccessStream
{
    private readonly Stream _source;
    private readonly ITeeRangeReadSource _ranges;
    private readonly int _windowSize;
    private readonly long _length;
    private readonly bool _leaveOpen;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _disposeLock = new();
    private Task? _disposeTask;
    private byte[]? _buffer;
    private Stream? _range;
    private long _start;
    private int _filled;
    private long _position;
    private int _disposed;

    /// <summary>Wraps a range-capable stream and lazily allocates up to one window of buffered bytes.</summary>
    /// <param name="source">A readable, seekable stream supporting independent ranges, or a readable file.</param>
    /// <param name="windowSize">Maximum bytes in one range and its buffer.</param>
    /// <param name="leaveOpen">Whether disposal leaves the source open.</param>
    public ReadAheadStream(Stream source, int windowSize = 4 * 1024 * 1024, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSize);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException("The source must be readable and seekable.", nameof(source));
        }

        _ranges = TeeRandomAccess.TryGetRangeReadSource(source)
            ?? throw new ArgumentException("The source must support independent range reads.", nameof(source));
        _source = source;
        _length = source.Length;
        _position = source.Position;
        _windowSize = windowSize;
        _leaveOpen = leaveOpen;
    }

    /// <inheritdoc/>
    public override bool CanRead => Volatile.Read(ref _disposed) == 0 && _source.CanRead;

    /// <inheritdoc/>
    public override bool CanSeek => CanRead;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public bool CanReadAt => CanRead;

    /// <inheritdoc/>
    public bool CanWriteAt => false;

    /// <inheritdoc/>
    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            return _length;
        }
    }

    /// <inheritdoc/>
    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return Volatile.Read(ref _position);
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            Seek(value, SeekOrigin.Begin);
        }
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        _gate.Wait();
        try
        {
            ThrowIfDisposed();
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(_length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (target < 0)
            {
                throw new IOException("Cannot seek before the start of the stream.");
            }
            if (target < _start || target > _start + _filled)
            {
                CloseRange();
            }

            Volatile.Write(ref _position, target);
            return target;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer) => ReadCore(buffer, null);

    /// <inheritdoc/>
    public int ReadAt(Span<byte> buffer, long offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return ReadCore(buffer, offset);
    }

    private int ReadCore(Span<byte> buffer, long? offset)
    {
        _gate.Wait();
        try
        {
            ThrowIfDisposed();
            long position = offset ?? _position;
            if (buffer.IsEmpty || position >= _length)
            {
                return 0;
            }
            EnsureWindowAsync(position, buffer.Length, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            return CopyFromWindow(buffer, position, advance: !offset.HasValue);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc/>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ReadCoreAsync(buffer, null, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<int> ReadAtAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return ReadCoreAsync(buffer, offset, cancellationToken);
    }

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, long? offset, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            long position = offset ?? _position;
            if (buffer.IsEmpty || position >= _length)
            {
                return 0;
            }
            await EnsureWindowAsync(position, buffer.Length, cancellationToken).ConfigureAwait(false);
            return CopyFromWindow(buffer.Span, position, advance: !offset.HasValue);
        }
        finally
        {
            _gate.Release();
        }
    }

    private int CopyFromWindow(Span<byte> destination, long position, bool advance)
    {
        int index = checked((int)(position - _start));
        int count = Math.Min(destination.Length, _filled - index);
        _buffer.AsSpan(index, count).CopyTo(destination);
        if (advance)
        {
            Volatile.Write(ref _position, position + count);
        }
        return count;
    }

    private async ValueTask EnsureWindowAsync(long position, int requested, CancellationToken cancellationToken)
    {
        if (!_source.CanRead)
        {
            throw new IOException("The range source is no longer readable.");
        }

        if (position >= _start && position < _start + _filled)
        {
            return;
        }

        using var linked = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token)
            : null;
        CancellationToken token = linked?.Token ?? _stop.Token;
        try
        {
            if (_range is null || position != _start + _filled)
            {
                await CloseRangeAsync().ConfigureAwait(false);
                _start = position;
                _filled = 0;
                int length = (int)Math.Min(_windowSize, _length - position);
                if (_buffer is null || _buffer.Length < length)
                {
                    _buffer = GC.AllocateUninitializedArray<byte>(length);
                }
                _range = await _ranges.OpenReadRangeAsync(position, length, token).ConfigureAwait(false);
            }

            int remaining = (int)Math.Min(_buffer!.Length - _filled, _length - position);
            // A tiny header read can begin consuming a long response without waiting for the whole window.
            int chunk = Math.Min(remaining, Math.Max(requested, 16 * 1024));
            int count = await _range.ReadAsync(_buffer.AsMemory(_filled, chunk), token).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("The source ended inside the reserved range.");
            }
            _filled += count;
            if (count == remaining)
            {
                await CloseRangeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            _filled = 0;
            await CloseRangeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc/>
    public override void Flush() => ThrowIfDisposed();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    public void WriteAt(ReadOnlySpan<byte> buffer, long offset) => throw new NotSupportedException();

    /// <inheritdoc/>
    public ValueTask WriteAtAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NotSupportedException());

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            BeginDispose(asynchronously: false).GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        await BeginDispose(asynchronously: true).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private Task BeginDispose(bool asynchronously)
    {
        lock (_disposeLock)
        {
            if (_disposeTask is { IsCompleted: true })
            {
                return Task.CompletedTask;
            }
            return _disposeTask ??= ReleaseResourcesAsync(asynchronously);
        }
    }

    private async Task ReleaseResourcesAsync(bool asynchronously)
    {
        Volatile.Write(ref _disposed, 1);
        try
        {
            if (asynchronously)
            {
                await _stop.CancelAsync().ConfigureAwait(false);
            }
            else
            {
                _stop.Cancel();
            }
        }
        finally
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (asynchronously)
                {
                    await CloseRangeAsync().ConfigureAwait(false);
                }
                else
                {
                    CloseRange();
                }
            }
            finally
            {
                _buffer = null;
                _stop.Dispose();
                _gate.Release();
                if (!_leaveOpen)
                {
                    if (asynchronously)
                    {
                        await _source.DisposeAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        _source.Dispose();
                    }
                }
            }
        }
    }

    private void CloseRange()
    {
        Stream? range = _range;
        _range = null;
        range?.Dispose();
    }

    private ValueTask CloseRangeAsync()
    {
        Stream? range = _range;
        _range = null;
        return range?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
