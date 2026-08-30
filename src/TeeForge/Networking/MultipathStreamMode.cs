namespace TeeForge.Networking;

/// <summary>Identifies how a logical byte sequence is distributed across active paths.</summary>
public enum MultipathStreamMode
{
    /// <summary>Sends every logical group over every active path.</summary>
    Raid1,

    /// <summary>Sends consecutive logical groups over successive active paths.</summary>
    Raid0,

    /// <summary>Sends systematic data shards and Reed-Solomon parity shards over distinct paths.</summary>
    ErasureCode,
}
