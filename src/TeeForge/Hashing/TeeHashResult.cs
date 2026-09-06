namespace TeeForge.Hashing;

/// <summary>Contains one immutable completed hash value and its text encodings.</summary>
public class TeeHashResult
{
    private readonly Lazy<string> _base32;
    private readonly Lazy<string> _base64;
    private readonly Lazy<string> _base64Url;
    private readonly byte[] _bytes;
    private readonly Lazy<string> _hex;

    /// <summary>Initializes an immutable hash result by copying the supplied digest.</summary>
    /// <param name="algorithm">The algorithm that produced the digest.</param>
    /// <param name="bytes">The completed digest bytes.</param>
    public TeeHashResult(TeeHashAlgorithmId algorithm, ReadOnlySpan<byte> bytes)
        : this(algorithm, bytes.ToArray())
    {
    }

    private TeeHashResult(TeeHashAlgorithmId algorithm, byte[] ownedBytes)
    {
        if (string.IsNullOrWhiteSpace(algorithm.Name))
        {
            throw new ArgumentException("A named hash algorithm is required.", nameof(algorithm));
        }

        Algorithm = algorithm;
        _bytes = ownedBytes;
        _hex = new Lazy<string>(() => Convert.ToHexString(_bytes));
        _base64 = new Lazy<string>(() => Convert.ToBase64String(_bytes));
        _base64Url = new Lazy<string>(() => System.Buffers.Text.Base64Url.EncodeToString(_bytes));
        _base32 = new Lazy<string>(() => EncodeBase32(_bytes));
    }

    /// <summary>Gets the algorithm that produced the digest.</summary>
    public TeeHashAlgorithmId Algorithm { get; }

    /// <summary>Gets read-only access to the completed digest bytes.</summary>
    public ReadOnlyMemory<byte> Bytes => _bytes;

    /// <summary>Gets the uppercase hexadecimal digest, computed on first access.</summary>
    public string Hex => _hex.Value;

    /// <summary>Gets the padded Base64 digest, computed on first access.</summary>
    public string Base64 => _base64.Value;

    /// <summary>Gets the unpadded URL-safe Base64 digest, computed on first access.</summary>
    public string Base64Url => _base64Url.Value;

    /// <summary>Gets the uppercase, padded RFC 4648 Base32 digest, computed on first access.</summary>
    public string Base32 => _base32.Value;

    internal static TeeHashResult FromOwnedBytes(TeeHashAlgorithmId algorithm, byte[] bytes) =>
        new(algorithm, bytes);

    private static string EncodeBase32(byte[] bytes)
    {
        int length = checked((int)(((long)bytes.Length + 4) / 5 * 8));
        return string.Create(length, bytes, static (characters, digest) =>
        {
            const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            int buffer = 0;
            int bits = 0;
            int position = 0;
            foreach (byte value in digest)
            {
                buffer = (buffer << 8) | value;
                bits += 8;
                while (bits >= 5)
                {
                    bits -= 5;
                    characters[position++] = Alphabet[(buffer >> bits) & 31];
                }
            }

            if (bits > 0)
            {
                characters[position++] = Alphabet[(buffer << (5 - bits)) & 31];
            }

            characters[position..].Fill('=');
        });
    }
}
