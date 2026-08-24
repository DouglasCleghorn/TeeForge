using System.Diagnostics.CodeAnalysis;

namespace TeeForge.RandomAccess;

/// <summary>Provides position-independent I/O over a stream's logical byte sequence.</summary>
/// <remarks>
/// Operations do not observe or modify <see cref="Stream.Position"/>. Concurrent calls are safe,
/// although an implementation may serialize their execution. Overlapping reads and writes do not
/// provide snapshot or transactional semantics unless a concrete implementation documents them.
/// </remarks>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "The interface is the agreed capability surface for Stream implementations.")]
public interface ITeeRandomAccessStream
{
    /// <summary>Gets whether explicit-offset reads are supported.</summary>
    bool CanReadAt { get; }

    /// <summary>Gets whether explicit-offset writes are supported.</summary>
    bool CanWriteAt { get; }

    /// <summary>Reads from an explicit logical offset without changing Position.</summary>
    int ReadAt(Span<byte> buffer, long offset);

    /// <summary>Asynchronously reads from an explicit logical offset without changing Position.</summary>
    ValueTask<int> ReadAtAsync(
        Memory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default);

    /// <summary>Writes at an explicit logical offset without changing Position.</summary>
    void WriteAt(ReadOnlySpan<byte> buffer, long offset);

    /// <summary>Asynchronously writes at an explicit logical offset without changing Position.</summary>
    ValueTask WriteAtAsync(
        ReadOnlyMemory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default);
}
