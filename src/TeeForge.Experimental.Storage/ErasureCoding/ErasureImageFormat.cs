namespace TeeForge.Experimental.Storage.ErasureCoding;

/// <summary>Identifies whether an <see cref="ErasureImage"/> stores member superblocks.</summary>
public enum ErasureImageFormat
{
    /// <summary>Stores only raw shard blocks; callers supply geometry and member order.</summary>
    Raw,

    /// <summary>Stores aligned, redundant member superblocks before shard data.</summary>
    SelfDescribing,
}
