using System.Security.Cryptography;

namespace TeeForge.Hashing;

/// <summary>Identifies a cryptographic hash by its .NET name or a TeeForge checksum.</summary>
/// <remarks>
/// Standard cryptographic algorithms have the same identity through either input type.
/// Cryptographic names remain distinct from checksum names, even when their text matches.
/// The default value is unnamed and cannot be used to compute or publish a hash.
/// </remarks>
public readonly record struct TeeHashAlgorithmId
{
    private readonly string? _name;

    /// <summary>Initializes an identifier for a supported hash or checksum.</summary>
    /// <param name="algorithm">The defined TeeForge algorithm.</param>
    public TeeHashAlgorithmId(TeeHashAlgorithm algorithm)
    {
        if (!Enum.IsDefined(algorithm))
        {
            throw new ArgumentOutOfRangeException(nameof(algorithm));
        }

        IsCryptographic = TeeHashAlgorithmAdapter.TryToHashAlgorithmName(algorithm, out HashAlgorithmName name);
        _name = IsCryptographic ? name.Name : algorithm.ToString();
    }

    /// <summary>Initializes a cryptographic identifier without restricting names to the TeeForge enum.</summary>
    /// <param name="algorithm">The nonempty .NET algorithm name; runtime support is checked when hashing starts.</param>
    public TeeHashAlgorithmId(HashAlgorithmName algorithm)
    {
        if (string.IsNullOrWhiteSpace(algorithm.Name))
        {
            throw new ArgumentException("A named hash algorithm is required.", nameof(algorithm));
        }

        _name = algorithm.Name;
        IsCryptographic = true;
    }

    /// <summary>Gets the algorithm name, or an empty string for the default identifier.</summary>
    public string Name => _name ?? string.Empty;

    /// <summary>Gets whether the identifier names a .NET cryptographic algorithm.</summary>
    public bool IsCryptographic { get; }

    /// <summary>Converts a TeeForge algorithm to its shared identifier.</summary>
    /// <param name="algorithm">The defined algorithm.</param>
    public static implicit operator TeeHashAlgorithmId(TeeHashAlgorithm algorithm) => new(algorithm);

    /// <summary>Converts a .NET cryptographic algorithm name to its shared identifier.</summary>
    /// <param name="algorithm">The nonempty cryptographic name.</param>
    public static implicit operator TeeHashAlgorithmId(HashAlgorithmName algorithm) => new(algorithm);

    /// <inheritdoc/>
    public override string ToString() => Name;
}
