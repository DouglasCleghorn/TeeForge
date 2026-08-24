using System.Security.Cryptography;

namespace TeeForge.Hashing;

/// <summary>Contains one immutable completed hash value and its text encodings.</summary>
public class TeeHashResult
{
    private readonly Lazy<string> _base64;
    private readonly byte[] _bytes;
    private readonly Lazy<string> _hex;

    /// <summary>Initializes an immutable hash result by copying the supplied digest.</summary>
    /// <param name="algorithm">The algorithm that produced the digest.</param>
    /// <param name="bytes">The completed digest bytes.</param>
    public TeeHashResult(HashAlgorithmName algorithm, ReadOnlySpan<byte> bytes)
        : this(algorithm, bytes.ToArray())
    {
    }

    private TeeHashResult(HashAlgorithmName algorithm, byte[] ownedBytes)
    {
        if (string.IsNullOrWhiteSpace(algorithm.Name))
        {
            throw new ArgumentException("A named hash algorithm is required.", nameof(algorithm));
        }

        Algorithm = algorithm;
        _bytes = ownedBytes;
        _hex = new Lazy<string>(() => Convert.ToHexString(_bytes));
        _base64 = new Lazy<string>(() => Convert.ToBase64String(_bytes));
    }

    /// <summary>Gets the algorithm that produced the digest.</summary>
    public HashAlgorithmName Algorithm { get; }

    /// <summary>Gets read-only access to the completed digest bytes.</summary>
    public ReadOnlyMemory<byte> Bytes => _bytes;

    /// <summary>Gets the uppercase hexadecimal digest, computed on first access.</summary>
    public string Hex => _hex.Value;

    /// <summary>Gets the padded Base64 digest, computed on first access.</summary>
    public string Base64 => _base64.Value;

    internal static TeeHashResult FromOwnedBytes(HashAlgorithmName algorithm, byte[] bytes) =>
        new(algorithm, bytes);
}
