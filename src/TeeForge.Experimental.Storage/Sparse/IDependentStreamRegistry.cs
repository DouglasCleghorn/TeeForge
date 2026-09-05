namespace TeeForge.Experimental.Storage.Sparse;

/// <summary>Maintains an advisory list of immediate known dependent streams.</summary>
public interface IDependentStreamRegistry
{
    /// <summary>Gets whether at least one dependent stream is registered.</summary>
    bool HasDependentStreams { get; }

    /// <summary>Gets a snapshot of registered immediate-child identifiers.</summary>
    IReadOnlyCollection<Guid> DependentStreamIds { get; }

    /// <summary>Idempotently registers an immediate child.</summary>
    void RegisterDependentStream(Guid id);

    /// <summary>Idempotently registers an immediate child.</summary>
    ValueTask RegisterDependentStreamAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Idempotently unregisters an immediate child.</summary>
    void UnregisterDependentStream(Guid id);

    /// <summary>Idempotently unregisters an immediate child.</summary>
    ValueTask UnregisterDependentStreamAsync(Guid id, CancellationToken cancellationToken = default);
}
