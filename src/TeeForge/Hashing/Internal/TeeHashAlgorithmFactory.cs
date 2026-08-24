using System.IO.Hashing;
using System.Security.Cryptography;

namespace TeeForge.Hashing.Internal;

internal interface IHashAccumulator : IDisposable
{
    void Append(ReadOnlySpan<byte> source);

    byte[] GetHashAndReset();
}

internal static class TeeHashAlgorithmFactory
{
    internal static IHashAccumulator Create(HashAlgorithmName algorithm) =>
        new CryptographicHashAccumulator(algorithm);

    internal static IHashAccumulator Create(TeeHashAlgorithm algorithm)
    {
        if (TeeHashAlgorithmAdapter.TryToHashAlgorithmName(algorithm, out HashAlgorithmName cryptographicName))
        {
            return Create(cryptographicName);
        }

        NonCryptographicHashAlgorithm hash = algorithm switch
        {
            TeeHashAlgorithm.Crc32 => new Crc32(),
            TeeHashAlgorithm.Crc64 => new Crc64(),
            TeeHashAlgorithm.XxHash32 => new XxHash32(),
            TeeHashAlgorithm.XxHash64 => new XxHash64(),
            TeeHashAlgorithm.XxHash3 => new XxHash3(),
            TeeHashAlgorithm.XxHash128 => new XxHash128(),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };

        return new NonCryptographicHashAccumulator(hash);
    }

    private sealed class CryptographicHashAccumulator : IHashAccumulator
    {
        private readonly IncrementalHash _hash;

        internal CryptographicHashAccumulator(HashAlgorithmName algorithm) =>
            _hash = IncrementalHash.CreateHash(algorithm);

        public void Append(ReadOnlySpan<byte> source) => _hash.AppendData(source);

        public byte[] GetHashAndReset() => _hash.GetHashAndReset();

        public void Dispose() => _hash.Dispose();
    }

    private sealed class NonCryptographicHashAccumulator(NonCryptographicHashAlgorithm hash)
        : IHashAccumulator
    {
        public void Append(ReadOnlySpan<byte> source) => hash.Append(source);

        public byte[] GetHashAndReset() => hash.GetHashAndReset();

        public void Dispose()
        {
        }
    }
}
