namespace TeeForge.RandomAccess;

/// <summary>Opens independent bounded streams over explicit logical byte ranges.</summary>
public interface ITeeRangeReadSource
{
    /// <summary>Opens a read-only, forward-only stream over the requested logical range.</summary>
    /// <param name="offset">The zero-based logical offset at which the range begins.</param>
    /// <param name="length">The maximum requested range length.</param>
    /// <param name="cancellationToken">The token that cancels opening the range.</param>
    /// <returns>
    /// A stream bounded to the source length. A zero-length request or an offset at or beyond
    /// end of stream returns an empty stream.
    /// </returns>
    ValueTask<Stream> OpenReadRangeAsync(
        long offset,
        long length,
        CancellationToken cancellationToken = default);
}
