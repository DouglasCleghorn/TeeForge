using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using TeeForge.Hashing.Internal;
using TeeForge.Mirroring;

namespace TeeForge.Hashing;

#pragma warning disable RS0026 // Sequence overloads are distinguished by their closed algorithm element types.

/// <summary>
/// Provides a write-only buffered tee that publishes one or more hashes when disposed.
/// </summary>
/// <remarks>
/// Hashes describe the bytes observed by the internal hash destinations. Buffered retries
/// after a partial mirrored failure are therefore observed again.
/// </remarks>
public class TeeHashStream : TeeBufferedStream
{
    private readonly HashWriteStream[] _hashStreams;

    /// <summary>Initializes a cryptographic hashing tee over the supplied destinations.</summary>
    /// <param name="algorithm">The cryptographic hash algorithm.</param>
    /// <param name="results">Receives the stable results collection.</param>
    /// <param name="destinations">The writable destination streams.</param>
    public TeeHashStream(
        HashAlgorithmName algorithm,
        out TeeHashResults results,
        params Stream[] destinations)
        : this(CreateState(TeeHashAlgorithmFactory.Normalize([algorithm]), destinations, options: null), out results)
    {
    }

    /// <summary>Initializes a cryptographic hashing tee from algorithm and destination sequences.</summary>
    /// <param name="algorithms">The cryptographic hash algorithms.</param>
    /// <param name="results">Receives the stable results collection.</param>
    /// <param name="destinations">The writable destination streams.</param>
    /// <param name="options">The buffering and tee behavior, or <see langword="null"/> for defaults.</param>
    public TeeHashStream(
        IEnumerable<HashAlgorithmName> algorithms,
        out TeeHashResults results,
        IEnumerable<Stream> destinations,
        TeeBufferedStreamOptions? options = null)
        : this(CreateState(TeeHashAlgorithmFactory.Normalize(algorithms), destinations, options), out results)
    {
    }

    /// <summary>Initializes a TeeForge hashing tee over the supplied destinations.</summary>
    /// <param name="algorithm">The cryptographic or non-cryptographic hash algorithm.</param>
    /// <param name="results">Receives the stable results collection.</param>
    /// <param name="destinations">The writable destination streams.</param>
    public TeeHashStream(
        TeeHashAlgorithm algorithm,
        out TeeHashResults results,
        params Stream[] destinations)
        : this(CreateState(TeeHashAlgorithmFactory.Normalize([algorithm]), destinations, options: null), out results)
    {
    }

    /// <summary>Initializes a TeeForge hashing tee from algorithm and destination sequences.</summary>
    /// <param name="algorithms">The cryptographic or non-cryptographic hash algorithms.</param>
    /// <param name="results">Receives the stable results collection.</param>
    /// <param name="destinations">The writable destination streams.</param>
    /// <param name="options">The buffering and tee behavior, or <see langword="null"/> for defaults.</param>
    public TeeHashStream(
        IEnumerable<TeeHashAlgorithm> algorithms,
        out TeeHashResults results,
        IEnumerable<Stream> destinations,
        TeeBufferedStreamOptions? options = null)
        : this(CreateState(TeeHashAlgorithmFactory.Normalize(algorithms), destinations, options), out results)
    {
    }

    private TeeHashStream(ConstructionState state, out TeeHashResults results)
        : base(state.AllDestinations, state.Options)
    {
        _hashStreams = state.HashStreams;
        results = state.Results;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            base.Dispose(disposing);
            return;
        }

        ExceptionDispatchInfo? baseFailure = null;
        try
        {
            base.Dispose(disposing);
        }
        catch (Exception exception)
        {
            baseFailure = ExceptionDispatchInfo.Capture(exception);
        }

        List<ExceptionDispatchInfo>? hashFailures = null;
        foreach (HashWriteStream hashStream in _hashStreams)
        {
            try
            {
                hashStream.Dispose();
            }
            catch (Exception exception)
            {
                (hashFailures ??= []).Add(ExceptionDispatchInfo.Capture(exception));
            }
        }

        ThrowDisposeFailures(baseFailure, hashFailures);
    }

    private static ConstructionState CreateState(
        TeeHashAlgorithmId[] algorithms,
        IEnumerable<Stream> destinations,
        TeeBufferedStreamOptions? options)
    {
        ArgumentNullException.ThrowIfNull(destinations);
        Stream[] destinationArray = destinations.ToArray();
        ValidateDestinations(destinationArray, nameof(destinations));

        var results = new TeeHashResults();
        HashWriteStream[] hashStreams = HashCompletionCoordinator.CreateStreams(algorithms, results);
        Stream[] allDestinations = new Stream[destinationArray.Length + hashStreams.Length];
        destinationArray.CopyTo(allDestinations, 0);
        hashStreams.CopyTo(allDestinations, destinationArray.Length);

        return new ConstructionState(allDestinations, hashStreams, results, options ?? TeeBufferedStreamOptions.Default);
    }

    private static void ValidateDestinations(Stream[] destinations, string parameterName)
    {
        if (destinations.Length == 0)
        {
            throw new ArgumentException("At least one destination is required.", parameterName);
        }

        var identities = new HashSet<Stream>(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < destinations.Length; index++)
        {
            Stream destination = destinations[index]
                ?? throw new ArgumentException($"Destination at index {index} is null.", parameterName);

            if (!identities.Add(destination))
            {
                throw new ArgumentException(
                    $"Destination at index {index} is a duplicate object reference.",
                    parameterName);
            }

            if (!destination.CanWrite)
            {
                throw new ArgumentException(
                    $"Destination at index {index} does not support writing.",
                    parameterName);
            }
        }
    }

    private static void ThrowDisposeFailures(
        ExceptionDispatchInfo? baseFailure,
        List<ExceptionDispatchInfo>? hashFailures)
    {
        if (baseFailure is null && (hashFailures is null || hashFailures.Count == 0))
        {
            return;
        }

        if (baseFailure is not null && (hashFailures is null || hashFailures.Count == 0))
        {
            baseFailure.Throw();
        }

        if (baseFailure is null && hashFailures!.Count == 1)
        {
            hashFailures[0].Throw();
        }

        IEnumerable<Exception> failures = hashFailures is null
            ? [baseFailure!.SourceException]
            : baseFailure is null
                ? hashFailures.Select(static failure => failure.SourceException)
                : [baseFailure.SourceException, .. hashFailures.Select(static failure => failure.SourceException)];

        throw new AggregateException("TeeHashStream disposal failed.", failures);
    }

    private sealed record ConstructionState(
        Stream[] AllDestinations,
        HashWriteStream[] HashStreams,
        TeeHashResults Results,
        TeeBufferedStreamOptions Options);
}

#pragma warning restore RS0026
