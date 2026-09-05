namespace TeeForge.Experimental.Storage.Sparse;

/// <summary>Indicates that a supplied base does not match a differencing image's recorded parent.</summary>
public class DifferencingDiskImageBaseMismatchException : IOException
{
    /// <summary>Initializes the exception.</summary>
    public DifferencingDiskImageBaseMismatchException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes the exception.</summary>
    public DifferencingDiskImageBaseMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
