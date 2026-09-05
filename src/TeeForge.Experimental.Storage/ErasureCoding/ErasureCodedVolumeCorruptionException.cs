namespace TeeForge.Experimental.Storage.ErasureCoding;

/// <summary>Reports erasure-set metadata, journal, generation, or codeword corruption.</summary>
public class ErasureCodedVolumeCorruptionException : IOException
{
    /// <summary>Initializes a corruption exception.</summary>
    public ErasureCodedVolumeCorruptionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
