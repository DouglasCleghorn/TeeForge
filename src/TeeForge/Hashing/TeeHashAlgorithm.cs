namespace TeeForge.Hashing;

#pragma warning disable CA1707 // SHA3 member names intentionally match HashAlgorithmName.

/// <summary>Identifies a cryptographic or non-cryptographic hash supported by TeeForge.</summary>
public enum TeeHashAlgorithm
{
    /// <summary>MD5 cryptographic hash. Its collision resistance is broken; do not use it for security.</summary>
    MD5 = 1,

    /// <summary>SHA-1 cryptographic hash. Its collision resistance is broken; do not use it for security.</summary>
    SHA1 = 2,

    /// <summary>SHA-256 cryptographic hash from the SHA-2 family.</summary>
    SHA256 = 3,

    /// <summary>SHA-384 cryptographic hash from the SHA-2 family.</summary>
    SHA384 = 4,

    /// <summary>SHA-512 cryptographic hash from the SHA-2 family.</summary>
    SHA512 = 5,

    /// <summary>SHA3-256 cryptographic hash. Runtime support can depend on the current platform.</summary>
    SHA3_256 = 6,

    /// <summary>SHA3-384 cryptographic hash. Runtime support can depend on the current platform.</summary>
    SHA3_384 = 7,

    /// <summary>SHA3-512 cryptographic hash. Runtime support can depend on the current platform.</summary>
    SHA3_512 = 8,

    /// <summary>CRC-32 non-cryptographic checksum. It is not suitable for security purposes.</summary>
    Crc32 = 9,

    /// <summary>CRC-64 non-cryptographic checksum. It is not suitable for security purposes.</summary>
    Crc64 = 10,

    /// <summary>XXH32 fast non-cryptographic hash. It is not suitable for security purposes.</summary>
    XxHash32 = 11,

    /// <summary>XXH64 fast non-cryptographic hash. It is not suitable for security purposes.</summary>
    XxHash64 = 12,

    /// <summary>XXH3 64-bit fast non-cryptographic hash. It is not suitable for security purposes.</summary>
    XxHash3 = 13,

    /// <summary>XXH3 128-bit fast non-cryptographic hash. It is not suitable for security purposes.</summary>
    XxHash128 = 14,
}

#pragma warning restore CA1707
