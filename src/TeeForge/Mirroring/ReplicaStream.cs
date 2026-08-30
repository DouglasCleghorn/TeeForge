namespace TeeForge.Mirroring;

/// <summary>Replicates a forward-only byte sequence to one or more writable streams.</summary>
/// <remarks>
/// Separate operations are not serialized. Callers must apply the same ownership discipline
/// they would apply to an ordinary <see cref="Stream"/>.
/// </remarks>
public class ReplicaStream : Stream
{
    private TeeStream? _stream;

    /// <summary>Initializes a ReplicaStream that owns the supplied replicas.</summary>
    /// <param name="replicas">The streams that receive every write.</param>
    public ReplicaStream(params Stream[] replicas)
        : this((IEnumerable<Stream>)replicas, options: null)
    {
    }

    /// <summary>Initializes a ReplicaStream with explicit options.</summary>
    /// <param name="options">The stream options.</param>
    /// <param name="replicas">The streams that receive every write.</param>
    public ReplicaStream(ReplicaStreamOptions options, params Stream[] replicas)
        : this((IEnumerable<Stream>)replicas, options ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    /// <summary>Initializes a ReplicaStream from an enumerable of writable streams.</summary>
    /// <param name="replicas">The streams that receive every write.</param>
    /// <param name="options">The stream options, or <see langword="null"/> for defaults.</param>
    public ReplicaStream(IEnumerable<Stream> replicas, ReplicaStreamOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(replicas);

        Stream[] replicaArray = replicas.ToArray();
        ValidateReplicas(replicaArray);

        ReplicaStreamOptions effectiveOptions = options ?? ReplicaStreamOptions.Default;
        _stream = new TeeStream(
            new TeeStreamOptions(
                TeeStreamMismatchBehavior.ThrowAndContinue,
                effectiveOptions.SynchronousMode,
                effectiveOptions.LeaveOpen),
            replicaArray);
    }

    /// <inheritdoc/>
    public override bool CanRead => false;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanTimeout => _stream?.CanTimeout ?? false;

    /// <inheritdoc/>
    public override bool CanWrite => _stream?.CanWrite ?? false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException("ReplicaStream does not support seeking.");

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException("ReplicaStream does not support seeking.");
        set => throw new NotSupportedException("ReplicaStream does not support seeking.");
    }

    /// <inheritdoc/>
    public override int WriteTimeout
    {
        get => GetStream().WriteTimeout;
        set => GetStream().WriteTimeout = value;
    }

    /// <inheritdoc/>
    public override void Flush() => GetStream().Flush();

    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        GetStream().FlushAsync(cancellationToken);

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("ReplicaStream is write-only.");

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer) =>
        throw new NotSupportedException("ReplicaStream is write-only.");

    /// <inheritdoc/>
    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        Task.FromException<int>(new NotSupportedException("ReplicaStream is write-only."));

    /// <inheritdoc/>
    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<int>(new NotSupportedException("ReplicaStream is write-only."));

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("ReplicaStream does not support seeking.");

    /// <inheritdoc/>
    public override void SetLength(long value) =>
        throw new NotSupportedException("ReplicaStream does not support seeking.");

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) =>
        GetStream().Write(buffer, offset, count);

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer) => GetStream().Write(buffer);

    /// <inheritdoc/>
    public override void WriteByte(byte value) => GetStream().WriteByte(value);

    /// <inheritdoc/>
    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        GetStream().WriteAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc/>
    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        GetStream().WriteAsync(buffer, cancellationToken);

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing)
            {
                TeeStream? stream = Interlocked.Exchange(ref _stream, null);
                stream?.Dispose();
            }
        }
        finally
        {
            base.Dispose(disposing);
        }
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        TeeStream? stream = Interlocked.Exchange(ref _stream, null);
        try
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await base.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }

    private static void ValidateReplicas(Stream[] replicas)
    {
        if (replicas.Length == 0)
        {
            throw new ArgumentException("At least one replica is required.", nameof(replicas));
        }

        var identities = new HashSet<Stream>(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < replicas.Length; index++)
        {
            Stream replica = replicas[index]
                ?? throw new ArgumentException($"Replica at index {index} is null.", nameof(replicas));
            if (!identities.Add(replica))
            {
                throw new ArgumentException(
                    $"Replica at index {index} is a duplicate object reference.",
                    nameof(replicas));
            }

            if (!replica.CanWrite)
            {
                throw new ArgumentException($"Replica at index {index} is not writable.", nameof(replicas));
            }
        }
    }

    private TeeStream GetStream() =>
        _stream ?? throw new ObjectDisposedException(nameof(ReplicaStream));
}
