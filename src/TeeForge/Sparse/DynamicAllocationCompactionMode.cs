namespace TeeForge.Sparse;

/// <summary>Specifies how a <see cref="DynamicAllocationStream"/> reclaims physical blocks.</summary>
public enum DynamicAllocationCompactionMode
{
    /// <summary>Reclaims trim-marked blocks and packs reachable blocks without reading payload contents.</summary>
    Fast = 0,

    /// <summary>Performs fast compaction, then also reclaims allocated blocks whose payload is entirely zero.</summary>
    Slow = 1,
}
