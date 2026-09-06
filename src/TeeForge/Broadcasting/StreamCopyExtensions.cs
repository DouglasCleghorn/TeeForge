using System.Collections.Concurrent;
using TeeForge.Broadcasting.Internal;

namespace TeeForge.Broadcasting;

#pragma warning disable RS0026 // The explicit-options overload requires a distinct third parameter.

/// <summary>Copies one readable stream to multiple independently buffered destinations.</summary>
public static partial class StreamCopyExtensions
{
    /// <summary>Asynchronously broadcasts the remaining source bytes to every destination.</summary>
    /// <param name="source">The source, read from its current position.</param>
    /// <param name="destinations">One or more distinct writable streams, excluding the source.</param>
    /// <returns>A task that completes after the source pump and all destination copies stop.</returns>
    /// <remarks>All streams remain open. The method does not flush destinations. Failures stop the copy by default.</remarks>
    public static Task CopyToAsync(this Stream source, params Stream[] destinations) =>
        CopyToAsync(source, (IEnumerable<Stream>)destinations, BroadcastCopyOptions.Default);

    /// <summary>Asynchronously broadcasts the remaining source bytes with cancellation.</summary>
    /// <param name="source">The source, read from its current position.</param>
    /// <param name="destinations">A nonempty collection of distinct writable streams, excluding the source.</param>
    /// <param name="cancellationToken">Cancellation of the entire copy.</param>
    /// <returns>A task that completes after the source pump and all destination copies stop.</returns>
    public static Task CopyToAsync(
        this Stream source,
        IEnumerable<Stream> destinations,
        CancellationToken cancellationToken = default) =>
        CopyToAsync(source, destinations, BroadcastCopyOptions.Default, cancellationToken);

    /// <summary>Asynchronously broadcasts the remaining source bytes with explicit buffering and failure behavior.</summary>
    /// <param name="source">The source, read from its current position.</param>
    /// <param name="destinations">A nonempty collection of distinct writable streams, excluding the source.</param>
    /// <param name="options">The shared buffer and destination failure options.</param>
    /// <param name="cancellationToken">Cancellation of the entire copy, independently of its destination failure policy.</param>
    /// <returns>A task that completes after the source pump and all destination copies stop.</returns>
    /// <remarks>
    /// The destination collection is snapshotted and validated before source I/O begins. All caller-owned streams
    /// remain open and destinations are not flushed. Failures are aggregated after started work stops; destination
    /// exceptions identify their original collection indexes. Continue mode finishes healthy destinations before
    /// throwing. Caller cancellation cancels the task when there are no non-cancellation failures to report.
    /// A failed copy can leave different partial contents at its destinations; writes are not rolled back.
    /// </remarks>
    public static Task CopyToAsync(
        this Stream source,
        IEnumerable<Stream> destinations,
        BroadcastCopyOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Stream[] selected = ValidateDestinations(source, destinations);
        return CopyCoreAsync(source, selected, options, cancellationToken);
    }

    private static Stream[] ValidateDestinations(Stream source, IEnumerable<Stream> destinations)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destinations);
        Stream[] selected = destinations.ToArray();
        if (selected.Length == 0)
        {
            throw new ArgumentException("At least one destination is required.", nameof(destinations));
        }

        BroadcastStream.ValidateSource(source, selected.Length);
        var identities = new HashSet<Stream>(ReferenceEqualityComparer.Instance) { source };
        for (int index = 0; index < selected.Length; index++)
        {
            Stream destination = selected[index]
                ?? throw new ArgumentException($"Destination at index {index} is null.", nameof(destinations));
            if (!identities.Add(destination))
            {
                throw new ArgumentException($"Destination at index {index} is duplicated or is the source.", nameof(destinations));
            }

            if (!destination.CanWrite)
            {
                throw new ArgumentException($"Destination at index {index} is not writable.", nameof(destinations));
            }
        }

        return selected;
    }

    private static async Task CopyCoreAsync(
        Stream source,
        Stream[] destinations,
        BroadcastCopyOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var broadcast = new BroadcastStream(source, destinations.Length, options.StreamOptions, stop.Token);
        await CopyToDestinationsAsync(broadcast, destinations, options, stop, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyToDestinationsAsync(
        BroadcastStream broadcast,
        Stream[] destinations,
        BroadcastCopyOptions options,
        CancellationTokenSource stop,
        CancellationToken cancellationToken)
    {
        var destinationFailures = new BroadcastCopyDestinationException?[destinations.Length];
        var otherFailures = new ConcurrentQueue<Exception>();
        var copies = new Task[destinations.Length];
        for (int index = 0; index < destinations.Length; index++)
        {
            copies[index] = CopyDestinationAsync(index);
        }

        await Task.WhenAll(copies).ConfigureAwait(false);
        try
        {
            await broadcast.Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested || destinationFailures.All(static failure => failure is not null))
        {
        }
        catch (Exception exception)
        {
            otherFailures.Enqueue(exception);
        }

        // The same source exception is observed by the pump and potentially several readers.
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var failures = new List<Exception>();
        foreach (Exception exception in otherFailures)
        {
            if (seen.Add(exception))
            {
                failures.Add(exception);
            }
        }

        failures.AddRange(destinationFailures.OfType<BroadcastCopyDestinationException>());
        if (failures.Count != 0)
        {
            throw new AggregateException("Broadcast copy failed.", failures);
        }

        cancellationToken.ThrowIfCancellationRequested();

        async Task CopyDestinationAsync(int index)
        {
            Stream reader = broadcast.Readers[index];
            using var destination = new CopyDestinationStream(destinations[index], index);
            try
            {
                await reader.CopyToAsync(destination, options.BufferSize, stop.Token).ConfigureAwait(false);
            }
            catch (BroadcastCopyDestinationException exception) when (ReferenceEquals(exception, destination.Failure))
            {
                destinationFailures[index] = exception;
                if (options.FailureBehavior == BroadcastCopyFailureBehavior.Stop)
                {
                    try
                    {
                        await stop.CancelAsync().ConfigureAwait(false);
                    }
                    catch (Exception cancellationFailure)
                    {
                        otherFailures.Enqueue(cancellationFailure);
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                otherFailures.Enqueue(exception);
            }
            finally
            {
                try
                {
                    await reader.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    otherFailures.Enqueue(exception);
                }
            }
        }
    }
}

#pragma warning restore RS0026
