namespace TeeForge.RandomAccess;

/// <summary>Specifies how an <see cref="HttpRandomAccessStream"/> validates one remote representation.</summary>
public enum HttpRepresentationValidationMode
{
    /// <summary>Uses a strong ETag or Last-Modified value when the server supplies one.</summary>
    WhenAvailable = 0,

    /// <summary>Requires a strong ETag and fails opening when none is supplied.</summary>
    RequireStrongValidator = 1,

    /// <summary>Does not use an entity validator; response ranges and total length are still validated.</summary>
    None = 2,
}
