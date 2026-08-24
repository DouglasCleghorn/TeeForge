namespace TeeForge.Mirroring;

/// <summary>Specifies how one synchronous <see cref="TeeStream"/> operation is dispatched.</summary>
public enum TeeStreamSynchronousMode
{
    /// <summary>Invokes destinations in their deterministic index order.</summary>
    Sequential = 0,

    /// <summary>Invokes independent destination operations concurrently on thread-pool threads.</summary>
    Concurrent = 1,
}
