using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using TeeForge.Broadcasting;
using TeeForge.Broadcasting.Internal;
using TeeForge.Hashing.Internal;

namespace TeeForge.Hashing;

#pragma warning disable RS0026 // Algorithm sequences have distinct closed element types.

/// <summary>Broadcasts a readable source while computing one set of hashes for the entire broadcast.</summary>
/// <remarks>
/// Each source byte is hashed once before entering the shared buffer, independently of reader positions.
/// Results are published together at source EOF, before Completion succeeds. Failure, cancellation,
/// or abandonment before EOF leaves results incomplete. Disposal never drains the source.
/// </remarks>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "BroadcastHashStream is the hashing specialization of the broadcast endpoint owner.")]
public class BroadcastHashStream : BroadcastStream
{
    /// <summary>Starts a broadcast with one explicitly selected cryptographic hash.</summary>
    /// <param name="algorithm">The cryptographic algorithm.</param>
    /// <param name="results">Receives the stable results collection, published at source EOF.</param>
    /// <param name="source">The readable source.</param>
    /// <param name="readerCount">The positive number of independent readers.</param>
    /// <param name="options">The buffering and source ownership options.</param>
    /// <param name="cancellationToken">Cancellation of the entire broadcast.</param>
    public BroadcastHashStream(
        HashAlgorithmName algorithm,
        out TeeHashResults results,
        Stream source,
        int readerCount,
        BroadcastStreamOptions? options = null,
        CancellationToken cancellationToken = default)
        : this(TeeHashAlgorithmFactory.Normalize([algorithm]), out results, source, readerCount, options, cancellationToken)
    {
    }

    /// <summary>Starts a broadcast with explicitly selected cryptographic hashes.</summary>
    /// <param name="algorithms">The ordered, nonempty collection of distinct algorithms.</param>
    /// <param name="results">Receives the stable results collection, published at source EOF.</param>
    /// <param name="source">The readable source.</param>
    /// <param name="readerCount">The positive number of independent readers.</param>
    /// <param name="options">The buffering and source ownership options.</param>
    /// <param name="cancellationToken">Cancellation of the entire broadcast.</param>
    public BroadcastHashStream(
        IEnumerable<HashAlgorithmName> algorithms,
        out TeeHashResults results,
        Stream source,
        int readerCount,
        BroadcastStreamOptions? options = null,
        CancellationToken cancellationToken = default)
        : this(TeeHashAlgorithmFactory.Normalize(algorithms), out results, source, readerCount, options, cancellationToken)
    {
    }

    /// <summary>Starts a broadcast with one explicitly selected cryptographic hash or checksum.</summary>
    /// <param name="algorithm">The hash or checksum algorithm.</param>
    /// <param name="results">Receives the stable results collection, published at source EOF.</param>
    /// <param name="source">The readable source.</param>
    /// <param name="readerCount">The positive number of independent readers.</param>
    /// <param name="options">The buffering and source ownership options.</param>
    /// <param name="cancellationToken">Cancellation of the entire broadcast.</param>
    public BroadcastHashStream(
        TeeHashAlgorithm algorithm,
        out TeeHashResults results,
        Stream source,
        int readerCount,
        BroadcastStreamOptions? options = null,
        CancellationToken cancellationToken = default)
        : this(TeeHashAlgorithmFactory.Normalize([algorithm]), out results, source, readerCount, options, cancellationToken)
    {
    }

    /// <summary>Starts a broadcast with an ordered mixture of cryptographic hashes and checksums.</summary>
    /// <param name="algorithms">The ordered, nonempty collection of distinct algorithms.</param>
    /// <param name="results">Receives the stable results collection, published at source EOF.</param>
    /// <param name="source">The readable source.</param>
    /// <param name="readerCount">The positive number of independent readers.</param>
    /// <param name="options">The buffering and source ownership options.</param>
    /// <param name="cancellationToken">Cancellation of the entire broadcast.</param>
    public BroadcastHashStream(
        IEnumerable<TeeHashAlgorithm> algorithms,
        out TeeHashResults results,
        Stream source,
        int readerCount,
        BroadcastStreamOptions? options = null,
        CancellationToken cancellationToken = default)
        : this(TeeHashAlgorithmFactory.Normalize(algorithms), out results, source, readerCount, options, cancellationToken)
    {
    }

    internal static BroadcastHashStream Create(
        TeeHashAlgorithmId[] algorithms,
        out TeeHashResults results,
        Stream source,
        int readerCount,
        BroadcastStreamOptions options,
        CancellationToken cancellationToken) =>
        new(algorithms, out results, source, readerCount, options, cancellationToken);

    private BroadcastHashStream(
        TeeHashAlgorithmId[] algorithms,
        out TeeHashResults results,
        Stream source,
        int readerCount,
        BroadcastStreamOptions? options,
        CancellationToken cancellationToken)
        : this(CreateState(algorithms, source, readerCount), out results, source, readerCount, options, cancellationToken)
    {
    }

    private BroadcastHashStream(
        ConstructionState state,
        out TeeHashResults results,
        Stream source,
        int readerCount,
        BroadcastStreamOptions? options,
        CancellationToken cancellationToken)
        : base(source, readerCount, options, state.Observer, cancellationToken)
    {
        results = state.Results;
    }

    private static ConstructionState CreateState(TeeHashAlgorithmId[] algorithms, Stream source, int readerCount)
    {
        ValidateSource(source, readerCount);
        var results = new TeeHashResults();
        HashWriteStream[] streams = HashCompletionCoordinator.CreateStreams(algorithms, results);
        return new ConstructionState(new HashObserver(streams), results);
    }

    private sealed record ConstructionState(IBroadcastObserver Observer, TeeHashResults Results);

    private sealed class HashObserver(HashWriteStream[] streams) : IBroadcastObserver
    {
        public void Append(ReadOnlySpan<byte> bytes)
        {
            foreach (HashWriteStream stream in streams)
            {
                stream.Write(bytes);
            }
        }

        public void Complete()
        {
            foreach (HashWriteStream stream in streams)
            {
                stream.Dispose();
            }
        }

        public void Dispose()
        {
            foreach (HashWriteStream stream in streams)
            {
                stream.DisposeWithoutFinalizing();
            }
        }
    }
}

#pragma warning restore RS0026
