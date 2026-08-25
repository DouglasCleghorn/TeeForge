namespace TeeForge.ErasureCoding;

/// <summary>Reports erasure-set metadata, journal, generation, or codeword corruption.</summary>
public class ErasureCodeStreamCorruptionException : IOException
{
    /// <summary>Initializes a corruption exception.</summary>
    public ErasureCodeStreamCorruptionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
