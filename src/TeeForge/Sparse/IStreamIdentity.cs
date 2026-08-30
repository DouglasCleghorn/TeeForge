namespace TeeForge.Sparse;

/// <summary>Exposes stable stream identity and caller-visible data identity.</summary>
public interface IStreamIdentity
{
    /// <summary>Gets the immutable stream identifier.</summary>
    Guid Id { get; }

    /// <summary>Gets the identifier for the current caller-visible data generation.</summary>
    Guid DataWriteId { get; }
}
