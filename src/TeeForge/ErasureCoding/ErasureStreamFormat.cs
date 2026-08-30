namespace TeeForge.ErasureCoding;

/// <summary>Identifies whether an <see cref="ErasureStream"/> stores member superblocks.</summary>
public enum ErasureStreamFormat
{
    /// <summary>Stores only raw shard blocks; callers supply geometry and member order.</summary>
    Raw,

    /// <summary>Stores aligned, redundant member superblocks before shard data.</summary>
    SelfDescribing,
}
