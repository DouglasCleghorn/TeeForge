using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using TeeForge.Hashing.Internal;

namespace TeeForge.Hashing;

#pragma warning disable RS0026 // Algorithm sequences have distinct closed element types.

/// <summary>Hashes bytes as a consumer reads them from a forward-only view of a stream.</summary>
/// <remarks>
/// Reading starts at the source's current position. A nonempty read that observes EOF publishes
/// all results together. Reading exactly the payload length requires one more read to observe EOF.
/// Empty reads and early disposal leave an incomplete result collection unchanged. Reads and
/// disposal are serialized. Cancel the active read's token to interrupt it before disposal.
/// An exception from the source, including cancellation during its read, ends hashing because
/// the source may already have advanced. Later reads rethrow the failure. Cancellation before
/// reaching the source leaves the wrapper usable. Only read from the source through this wrapper.
/// </remarks>
public class HashingReadStream : Stream
{
    private readonly Stream _source;
    private readonly bool _leaveOpen;
    private readonly HashObserver _hashes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ExceptionDispatchInfo? _failure;
    private long _bytesRead;
    private bool _disposed;

    /// <summary>Wraps a readable source and computes a cryptographic hash or checksum on demand.</summary>
    /// <param name="source">The readable stream.</param>
    /// <param name="algorithm">The hash or checksum algorithm.</param>
    /// <param name="leaveOpen">Whether disposal leaves the source open.</param>
    public HashingReadStream(Stream source, TeeHashAlgorithm algorithm, bool leaveOpen = false)
        : this(source, TeeHashAlgorithmFactory.Normalize([algorithm]), leaveOpen)
    {
    }

    /// <summary>Wraps a readable source and computes an ordered set of hashes and checksums on demand.</summary>
    /// <param name="source">The readable stream.</param>
    /// <param name="algorithms">The nonempty collection of distinct algorithms.</param>
    /// <param name="leaveOpen">Whether disposal leaves the source open.</param>
    public HashingReadStream(Stream source, IEnumerable<TeeHashAlgorithm> algorithms, bool leaveOpen = false)
        : this(source, TeeHashAlgorithmFactory.Normalize(algorithms), leaveOpen)
    {
    }

    /// <summary>Wraps a readable source and computes the named cryptographic hash on demand.</summary>
    /// <param name="source">The readable stream.</param>
    /// <param name="algorithm">The cryptographic algorithm.</param>
    /// <param name="leaveOpen">Whether disposal leaves the source open.</param>
    public HashingReadStream(Stream source, HashAlgorithmName algorithm, bool leaveOpen = false)
        : this(source, TeeHashAlgorithmFactory.Normalize([algorithm]), leaveOpen)
    {
    }

    /// <summary>Wraps a readable source and computes an ordered set of named cryptographic hashes on demand.</summary>
    /// <param name="source">The readable stream.</param>
    /// <param name="algorithms">The nonempty collection of distinct cryptographic algorithms.</param>
    /// <param name="leaveOpen">Whether disposal leaves the source open.</param>
    public HashingReadStream(Stream source, IEnumerable<HashAlgorithmName> algorithms, bool leaveOpen = false)
        : this(source, TeeHashAlgorithmFactory.Normalize(algorithms), leaveOpen)
    {
    }

    private HashingReadStream(Stream source, TeeHashAlgorithmId[] algorithms, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The source must be readable.", nameof(source));
        }

        _source = source;
        _leaveOpen = leaveOpen;
        _hashes = new HashObserver(algorithms, Results);
    }

    /// <summary>Gets the results collection, published at successful EOF and accessible after disposal.</summary>
    public TeeHashResults Results { get; } = new();

    /// <summary>Gets the number of bytes hashed and returned by successful reads, including before EOF or after disposal.</summary>
    public long BytesRead => Volatile.Read(ref _bytesRead);

    /// <inheritdoc/>
    public override bool CanRead => !Volatile.Read(ref _disposed) && Volatile.Read(ref _failure) is null;

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

    /// <inheritdoc/>
    public override int ReadByte()
    {
        Span<byte> buffer = stackalloc byte[1];
        return Read(buffer) == 0 ? -1 : buffer[0];
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
        _gate.Wait();
        try
        {
            if (!ShouldRead(buffer.Length))
            {
                return 0;
            }

            try
            {
                return Observe(buffer, _source.Read(buffer));
            }
            catch (Exception error)
            {
                Fail(error);
                throw;
            }
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
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ShouldRead(buffer.Length))
            {
                return 0;
            }

            try
            {
                int count = await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                return Observe(buffer.Span, count);
            }
            catch (Exception error)
            {
                Fail(error);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool ShouldRead(int length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _failure?.Throw();
        return length != 0 && !Results.IsComplete;
    }

    private int Observe(ReadOnlySpan<byte> buffer, int count)
    {
        if (count == 0)
        {
            _hashes.Complete();
        }
        else
        {
            _hashes.Append(buffer[..count]);
            Volatile.Write(ref _bytesRead, checked(_bytesRead + count));
        }
        return count;
    }

    private void Fail(Exception error)
    {
        Volatile.Write(ref _failure, ExceptionDispatchInfo.Capture(error));
        _hashes.Dispose();
    }

    /// <inheritdoc/>
    public override void Flush() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeResourcesAsync(asynchronously: false).GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        await DisposeResourcesAsync(asynchronously: true).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task DisposeResourcesAsync(bool asynchronously)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            Volatile.Write(ref _disposed, true);
            _hashes.Dispose();
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
        finally
        {
            _gate.Release();
        }
    }
}

#pragma warning restore RS0026
