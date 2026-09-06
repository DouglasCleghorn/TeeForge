using System.Security.Cryptography;
using TeeForge.Hashing;
using TeeForge.Hashing.Internal;

namespace TeeForge.Broadcasting;

#pragma warning disable RS0026 // Required algorithm and destination parameter types distinguish these overloads.

public static partial class StreamCopyExtensions
{
    /// <summary>Copies to one or more destinations and returns the selected hash of the source sequence.</summary>
    /// <param name="source">The source, read from its current position.</param>
    /// <param name="algorithm">The explicitly selected hash or checksum.</param>
    /// <param name="destinations">One or more distinct writable destinations, excluding the source.</param>
    /// <returns>The completed hash collection after successful copying to every destination.</returns>
    public static Task<TeeHashResults> CopyToAsync(
        this Stream source, TeeHashAlgorithm algorithm, params Stream[] destinations) =>
        CopyToAsync(source, [algorithm], (IEnumerable<Stream>)destinations);

    /// <summary>Copies to one destination and returns the selected hash of the source sequence.</summary>
    /// <param name="source">The source, read from its current position.</param>
    /// <param name="algorithm">The explicitly selected hash or checksum.</param>
    /// <param name="destination">The writable destination, distinct from the source.</param>
    /// <param name="options">The buffering and failure options, or null for defaults.</param>
    /// <param name="cancellationToken">Cancellation of the entire copy.</param>
    /// <returns>The completed hash collection after successful copying.</returns>
    public static Task<TeeHashResults> CopyToAsync(
        this Stream source,
        TeeHashAlgorithm algorithm,
        Stream destination,
        BroadcastCopyOptions? options = null,
        CancellationToken cancellationToken = default) =>
        CopyToAsync(source, [algorithm], (IEnumerable<Stream>)[destination], options, cancellationToken);

    /// <summary>Copies to a destination collection and returns the selected hash of the source sequence.</summary>
    /// <param name="source">The source, read from its current position.</param>
    /// <param name="algorithm">The explicitly selected hash or checksum.</param>
    /// <param name="destinations">A nonempty collection of distinct writable destinations, excluding the source.</param>
    /// <param name="options">The buffering and failure options, or null for defaults.</param>
    /// <param name="cancellationToken">Cancellation of the entire copy.</param>
    /// <returns>The completed hash collection after successful copying to every destination.</returns>
    public static Task<TeeHashResults> CopyToAsync(
        this Stream source,
        TeeHashAlgorithm algorithm,
        IEnumerable<Stream> destinations,
        BroadcastCopyOptions? options = null,
        CancellationToken cancellationToken = default) =>
        CopyToAsync(source, [algorithm], destinations, options, cancellationToken);

    /// <summary>Copies to one or more destinations and returns the selected hashes of the source sequence.</summary>
    /// <param name="source">The source, read from its current position.</param>
    /// <param name="algorithms">The ordered, nonempty selection of distinct hashes and checksums.</param>
    /// <param name="destinations">One or more distinct writable destinations, excluding the source.</param>
    /// <returns>The completed hash collection after successful copying to every destination.</returns>
    public static Task<TeeHashResults> CopyToAsync(
        this Stream source, IEnumerable<TeeHashAlgorithm> algorithms, params Stream[] destinations) =>
        CopyToAsync(source, algorithms, (IEnumerable<Stream>)destinations);

    /// <summary>Copies to one destination and returns the selected hashes of the source sequence.</summary>
    /// <param name="source">The source, read from its current position.</param>
    /// <param name="algorithms">The ordered, nonempty selection of distinct hashes and checksums.</param>
    /// <param name="destination">The writable destination, distinct from the source.</param>
    /// <param name="options">The buffering and failure options, or null for defaults.</param>
    /// <param name="cancellationToken">Cancellation of the entire copy.</param>
    /// <returns>The completed hash collection after successful copying.</returns>
    public static Task<TeeHashResults> CopyToAsync(
        this Stream source,
        IEnumerable<TeeHashAlgorithm> algorithms,
        Stream destination,
        BroadcastCopyOptions? options = null,
        CancellationToken cancellationToken = default) =>
        CopyToAsync(source, algorithms, (IEnumerable<Stream>)[destination], options, cancellationToken);

    /// <summary>Copies to a destination collection and returns one set of hashes for the source sequence.</summary>
    /// <param name="source">The source, read from its current position.</param>
    /// <param name="algorithms">The ordered, nonempty selection of distinct hashes and checksums.</param>
    /// <param name="destinations">A nonempty collection of distinct writable destinations, excluding the source.</param>
    /// <param name="options">The buffering and failure options, or null for defaults.</param>
    /// <param name="cancellationToken">Cancellation of the entire copy.</param>
    /// <returns>The completed hash collection after source EOF and successful copying to every destination.</returns>
    /// <remarks>
    /// Each source byte is hashed once, independently of destination progress. Streams remain open and destinations
    /// are not flushed. Failure and cancellation follow the ordinary multi-destination CopyToAsync contract;
    /// Continue mode still throws after finishing healthy destinations, so a failed copy does not return hashes.
    /// </remarks>
    public static Task<TeeHashResults> CopyToAsync(
        this Stream source,
        IEnumerable<TeeHashAlgorithm> algorithms,
        IEnumerable<Stream> destinations,
        BroadcastCopyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Stream[] selectedDestinations = ValidateDestinations(source, destinations);
        ArgumentNullException.ThrowIfNull(algorithms);
        TeeHashAlgorithmId[] selectedAlgorithms = TeeHashAlgorithmFactory.Normalize(algorithms);
        return CopyWithHashesAsync(source, selectedAlgorithms, selectedDestinations, options ?? BroadcastCopyOptions.Default, cancellationToken);
    }

    /// <summary>Copies to one or more destinations and returns the selected cryptographic hash.</summary>
    /// <param name="source">The source, read from its current position.</param>
    /// <param name="algorithm">The explicitly selected cryptographic hash.</param>
    /// <param name="destinations">One or more distinct writable destinations, excluding the source.</param>
    /// <returns>The completed hash collection after successful copying to every destination.</returns>
    public static Task<TeeHashResults> CopyToAsync(
        this Stream source, HashAlgorithmName algorithm, params Stream[] destinations) =>
        CopyToAsync(source, [algorithm], (IEnumerable<Stream>)destinations);

    /// <summary>Copies to one destination and returns the selected cryptographic hash.</summary>
    /// <param name="source">The source, read from its current position.</param>
    /// <param name="algorithm">The explicitly selected cryptographic hash.</param>
    /// <param name="destination">The writable destination, distinct from the source.</param>
    /// <param name="options">The buffering and failure options, or null for defaults.</param>
    /// <param name="cancellationToken">Cancellation of the entire copy.</param>
    /// <returns>The completed hash collection after successful copying.</returns>
    public static Task<TeeHashResults> CopyToAsync(
        this Stream source,
        HashAlgorithmName algorithm,
        Stream destination,
        BroadcastCopyOptions? options = null,
        CancellationToken cancellationToken = default) =>
        CopyToAsync(source, [algorithm], (IEnumerable<Stream>)[destination], options, cancellationToken);

    /// <summary>Copies to a destination collection and returns the selected cryptographic hash.</summary>
    /// <param name="source">The source, read from its current position.</param>
    /// <param name="algorithm">The explicitly selected cryptographic hash.</param>
    /// <param name="destinations">A nonempty collection of distinct writable destinations, excluding the source.</param>
    /// <param name="options">The buffering and failure options, or null for defaults.</param>
    /// <param name="cancellationToken">Cancellation of the entire copy.</param>
    /// <returns>The completed hash collection after successful copying to every destination.</returns>
    public static Task<TeeHashResults> CopyToAsync(
        this Stream source,
        HashAlgorithmName algorithm,
        IEnumerable<Stream> destinations,
        BroadcastCopyOptions? options = null,
        CancellationToken cancellationToken = default) =>
        CopyToAsync(source, [algorithm], destinations, options, cancellationToken);

    /// <summary>Copies to one or more destinations and returns the selected cryptographic hashes.</summary>
    /// <param name="source">The source, read from its current position.</param>
    /// <param name="algorithms">The ordered, nonempty selection of distinct cryptographic hashes.</param>
    /// <param name="destinations">One or more distinct writable destinations, excluding the source.</param>
    /// <returns>The completed hash collection after successful copying to every destination.</returns>
    public static Task<TeeHashResults> CopyToAsync(
        this Stream source, IEnumerable<HashAlgorithmName> algorithms, params Stream[] destinations) =>
        CopyToAsync(source, algorithms, (IEnumerable<Stream>)destinations);

    /// <summary>Copies to one destination and returns the selected cryptographic hashes.</summary>
    /// <param name="source">The source, read from its current position.</param>
    /// <param name="algorithms">The ordered, nonempty selection of distinct cryptographic hashes.</param>
    /// <param name="destination">The writable destination, distinct from the source.</param>
    /// <param name="options">The buffering and failure options, or null for defaults.</param>
    /// <param name="cancellationToken">Cancellation of the entire copy.</param>
    /// <returns>The completed hash collection after successful copying.</returns>
    public static Task<TeeHashResults> CopyToAsync(
        this Stream source,
        IEnumerable<HashAlgorithmName> algorithms,
        Stream destination,
        BroadcastCopyOptions? options = null,
        CancellationToken cancellationToken = default) =>
        CopyToAsync(source, algorithms, (IEnumerable<Stream>)[destination], options, cancellationToken);

    /// <summary>Copies to a destination collection and returns one set of cryptographic hashes for the source sequence.</summary>
    /// <param name="source">The source, read from its current position.</param>
    /// <param name="algorithms">The ordered, nonempty selection of distinct cryptographic hashes.</param>
    /// <param name="destinations">A nonempty collection of distinct writable destinations, excluding the source.</param>
    /// <param name="options">The buffering and failure options, or null for defaults.</param>
    /// <param name="cancellationToken">Cancellation of the entire copy.</param>
    /// <returns>The completed hash collection after source EOF and successful copying to every destination.</returns>
    /// <remarks>Each source byte is hashed once. Streams remain open and unflushed. Failed or canceled copies do not return hashes.</remarks>
    public static Task<TeeHashResults> CopyToAsync(
        this Stream source,
        IEnumerable<HashAlgorithmName> algorithms,
        IEnumerable<Stream> destinations,
        BroadcastCopyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Stream[] selectedDestinations = ValidateDestinations(source, destinations);
        ArgumentNullException.ThrowIfNull(algorithms);
        TeeHashAlgorithmId[] selectedAlgorithms = TeeHashAlgorithmFactory.Normalize(algorithms);
        return CopyWithHashesAsync(source, selectedAlgorithms, selectedDestinations, options ?? BroadcastCopyOptions.Default, cancellationToken);
    }

    private static async Task<TeeHashResults> CopyWithHashesAsync(
        Stream source,
        TeeHashAlgorithmId[] algorithms,
        Stream[] destinations,
        BroadcastCopyOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var broadcast = BroadcastHashStream.Create(algorithms, out TeeHashResults results,
            source, destinations.Length, options.StreamOptions, stop.Token);
        await CopyToDestinationsAsync(broadcast, destinations, options, stop, cancellationToken).ConfigureAwait(false);
        return results;
    }

}

#pragma warning restore RS0026
