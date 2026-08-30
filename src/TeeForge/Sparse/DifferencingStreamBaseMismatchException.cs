namespace TeeForge.Sparse;

/// <summary>Indicates that a supplied base does not match a differencing image's recorded parent.</summary>
public class DifferencingStreamBaseMismatchException : IOException
{
    /// <summary>Initializes the exception.</summary>
    public DifferencingStreamBaseMismatchException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes the exception.</summary>
    public DifferencingStreamBaseMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
