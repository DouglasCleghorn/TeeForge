using System.Security.Cryptography;

namespace TeeForge.Hashing;

/// <summary>Converts standard cryptographic identifiers between TeeForge and .NET.</summary>
public static class TeeHashAlgorithmAdapter
{
    /// <summary>Converts a standard cryptographic algorithm name to its TeeForge identifier.</summary>
    /// <param name="algorithm">The standard cryptographic algorithm name.</param>
    /// <returns>The corresponding TeeForge identifier.</returns>
    /// <exception cref="ArgumentException"><paramref name="algorithm"/> is not a recognized standard name.</exception>
    public static TeeHashAlgorithm ToTeeHashAlgorithm(HashAlgorithmName algorithm)
    {
        if (TryToTeeHashAlgorithm(algorithm, out TeeHashAlgorithm result))
        {
            return result;
        }

        throw new ArgumentException(
            "The hash algorithm name is not a recognized standard cryptographic algorithm.",
            nameof(algorithm));
    }

    /// <summary>Attempts to convert a standard cryptographic algorithm name to its TeeForge identifier.</summary>
    /// <param name="algorithm">The standard cryptographic algorithm name.</param>
    /// <param name="result">The corresponding TeeForge identifier when conversion succeeds.</param>
    /// <returns><see langword="true"/> when the name is recognized; otherwise, <see langword="false"/>.</returns>
    public static bool TryToTeeHashAlgorithm(HashAlgorithmName algorithm, out TeeHashAlgorithm result)
    {
        result = algorithm.Name switch
        {
            "MD5" => TeeHashAlgorithm.MD5,
            "SHA1" => TeeHashAlgorithm.SHA1,
            "SHA256" => TeeHashAlgorithm.SHA256,
            "SHA384" => TeeHashAlgorithm.SHA384,
            "SHA512" => TeeHashAlgorithm.SHA512,
            "SHA3-256" => TeeHashAlgorithm.SHA3_256,
            "SHA3-384" => TeeHashAlgorithm.SHA3_384,
            "SHA3-512" => TeeHashAlgorithm.SHA3_512,
            _ => default,
        };

        return result != default;
    }

    /// <summary>Attempts to convert a TeeForge identifier to a standard cryptographic algorithm name.</summary>
    /// <param name="algorithm">The TeeForge identifier.</param>
    /// <param name="result">The standard name when conversion succeeds.</param>
    /// <returns>
    /// <see langword="true"/> for a cryptographic identifier; <see langword="false"/> for a
    /// non-cryptographic or undefined identifier.
    /// </returns>
    public static bool TryToHashAlgorithmName(TeeHashAlgorithm algorithm, out HashAlgorithmName result)
    {
        result = algorithm switch
        {
            TeeHashAlgorithm.MD5 => HashAlgorithmName.MD5,
            TeeHashAlgorithm.SHA1 => HashAlgorithmName.SHA1,
            TeeHashAlgorithm.SHA256 => HashAlgorithmName.SHA256,
            TeeHashAlgorithm.SHA384 => HashAlgorithmName.SHA384,
            TeeHashAlgorithm.SHA512 => HashAlgorithmName.SHA512,
            TeeHashAlgorithm.SHA3_256 => HashAlgorithmName.SHA3_256,
            TeeHashAlgorithm.SHA3_384 => HashAlgorithmName.SHA3_384,
            TeeHashAlgorithm.SHA3_512 => HashAlgorithmName.SHA3_512,
            _ => default,
        };

        return result.Name is not null;
    }
}
