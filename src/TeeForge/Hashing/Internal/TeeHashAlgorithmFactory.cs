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
    internal static TeeHashAlgorithmId[] Normalize(IEnumerable<HashAlgorithmName> algorithms) =>
        Normalize(algorithms, static algorithm => new TeeHashAlgorithmId(algorithm));

    internal static TeeHashAlgorithmId[] Normalize(IEnumerable<TeeHashAlgorithm> algorithms) =>
        Normalize(algorithms, static algorithm => new TeeHashAlgorithmId(algorithm));

    private static TeeHashAlgorithmId[] Normalize<TAlgorithm>(
        IEnumerable<TAlgorithm> algorithms, Func<TAlgorithm, TeeHashAlgorithmId> identify)
    {
        ArgumentNullException.ThrowIfNull(algorithms);
        var selected = new List<TeeHashAlgorithmId>();
        var identities = new HashSet<TeeHashAlgorithmId>();
        foreach (TAlgorithm algorithm in algorithms)
        {
            TeeHashAlgorithmId id;
            try
            {
                id = identify(algorithm);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException($"Hash algorithm at index {selected.Count} is invalid.", nameof(algorithms), exception);
            }

            if (!identities.Add(id))
            {
                throw new ArgumentException($"Hash algorithm at index {selected.Count} is duplicated.", nameof(algorithms));
            }

            selected.Add(id);
        }

        if (selected.Count == 0)
        {
            throw new ArgumentException("At least one hash algorithm is required.", nameof(algorithms));
        }

        return selected.ToArray();
    }

    internal static IHashAccumulator Create(TeeHashAlgorithmId algorithm)
    {
        if (algorithm.IsCryptographic)
        {
            return new CryptographicHashAccumulator(new HashAlgorithmName(algorithm.Name));
        }

        NonCryptographicHashAlgorithm hash = algorithm.Name switch
        {
            nameof(TeeHashAlgorithm.Crc32) => new Crc32(),
            nameof(TeeHashAlgorithm.Crc64) => new Crc64(),
            nameof(TeeHashAlgorithm.XxHash32) => new XxHash32(),
            nameof(TeeHashAlgorithm.XxHash64) => new XxHash64(),
            nameof(TeeHashAlgorithm.XxHash3) => new XxHash3(),
            nameof(TeeHashAlgorithm.XxHash128) => new XxHash128(),
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
