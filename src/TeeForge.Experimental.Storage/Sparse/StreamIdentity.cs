namespace TeeForge.Experimental.Storage.Sparse;

/// <summary>Describes the identity of a stream that does not implement <see cref="IStreamIdentity"/>.</summary>
public readonly record struct StreamIdentity
{
    /// <summary>Initializes a stream identity.</summary>
    public StreamIdentity(Guid id, Guid dataWriteId)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A stream identifier cannot be empty.", nameof(id));
        }

        if (dataWriteId == Guid.Empty)
        {
            throw new ArgumentException("A data-write identifier cannot be empty.", nameof(dataWriteId));
        }

        Id = id;
        DataWriteId = dataWriteId;
    }

    /// <summary>Gets the immutable stream identifier.</summary>
    public Guid Id { get; }

    /// <summary>Gets the current caller-visible data generation.</summary>
    public Guid DataWriteId { get; }
}
