namespace TeeForge.RandomAccess;

/// <summary>Indicates that a remote resource no longer matches its opened HTTP representation snapshot.</summary>
public class HttpRepresentationChangedException : IOException
{
    /// <summary>Initializes a new representation-change exception.</summary>
    public HttpRepresentationChangedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new representation-change exception with an inner failure.</summary>
    public HttpRepresentationChangedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
