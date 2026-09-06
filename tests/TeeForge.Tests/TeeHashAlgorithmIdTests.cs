using System.Security.Cryptography;
using TeeForge.Broadcasting;

namespace TeeForge.Tests;

public class TeeHashAlgorithmIdTests
{
    [Theory]
    [InlineData(TeeHashAlgorithm.MD5, "MD5")]
    [InlineData(TeeHashAlgorithm.SHA1, "SHA1")]
    [InlineData(TeeHashAlgorithm.SHA256, "SHA256")]
    [InlineData(TeeHashAlgorithm.SHA384, "SHA384")]
    [InlineData(TeeHashAlgorithm.SHA512, "SHA512")]
    [InlineData(TeeHashAlgorithm.SHA3_256, "SHA3-256")]
    [InlineData(TeeHashAlgorithm.SHA3_384, "SHA3-384")]
    [InlineData(TeeHashAlgorithm.SHA3_512, "SHA3-512")]
    public void Standard_crypto_identifiers_are_equal_across_input_forms(TeeHashAlgorithm algorithm, string name)
    {
        TeeHashAlgorithmId fromEnum = algorithm;
        TeeHashAlgorithmId fromDotNet = new HashAlgorithmName(name);

        Assert.Equal(fromEnum, fromDotNet);
        Assert.Equal(fromEnum.GetHashCode(), fromDotNet.GetHashCode());
        Assert.Equal(name, fromEnum.Name);
        Assert.True(fromEnum.IsCryptographic);
        Assert.True(fromEnum == fromDotNet);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Either_stream_input_form_supports_both_lookup_forms(bool useDotNet)
    {
        byte[] payload = [1, 2, 3];
        using var destination = new MemoryStream();
        TeeHashResults results;
        using (var stream = useDotNet
            ? new TeeHashStream(HashAlgorithmName.SHA256, out results, destination)
            : new TeeHashStream(TeeHashAlgorithm.SHA256, out results, destination))
        {
            stream.Write(payload);
        }

        Assert.Same(results[TeeHashAlgorithm.SHA256], results[HashAlgorithmName.SHA256]);
        Assert.True(results.ContainsKey(TeeHashAlgorithm.SHA256));
        Assert.True(results.ContainsKey(HashAlgorithmName.SHA256));
        Assert.True(results.TryGetValue(TeeHashAlgorithm.SHA256, out TeeHashResult fromEnum));
        Assert.True(results.TryGetValue(HashAlgorithmName.SHA256, out TeeHashResult fromDotNet));
        Assert.Same(fromEnum, fromDotNet);
        Assert.Equal(SHA256.HashData(payload), fromDotNet.Bytes.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Either_copy_input_form_returns_the_shared_result_model(bool useDotNet)
    {
        byte[] payload = [4, 5, 6];
        using var source = new MemoryStream(payload);
        using var destination = new MemoryStream();
        TeeHashResults results = await (useDotNet
            ? source.CopyToAsync(HashAlgorithmName.SHA256, destination)
            : source.CopyToAsync(TeeHashAlgorithm.SHA256, destination))
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Same(results[TeeHashAlgorithm.SHA256], results[HashAlgorithmName.SHA256]);
        Assert.Equal(SHA256.HashData(payload), results[HashAlgorithmName.SHA256].Bytes.ToArray());
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact]
    public void Custom_crypto_names_remain_distinct_from_checksums_with_the_same_name()
    {
        var results = new TeeHashResults();
        var customName = new HashAlgorithmName("Crc32");
        var crypto = new TeeHashResult(customName, [1]);
        var checksum = new TeeHashResult(TeeHashAlgorithm.Crc32, [2]);
        results.Publish([crypto, checksum]);

        Assert.Equal(2, results.Count);
        Assert.Same(crypto, results[customName]);
        Assert.Same(checksum, results[TeeHashAlgorithm.Crc32]);
        Assert.Equal(crypto.Algorithm.Name, checksum.Algorithm.Name);
        Assert.True(crypto.Algorithm.IsCryptographic);
        Assert.False(checksum.Algorithm.IsCryptographic);
        Assert.NotEqual(crypto.Algorithm, checksum.Algorithm);
        Assert.NotEqual(crypto.Algorithm, new TeeHashAlgorithmId(new HashAlgorithmName("CRC32")));
    }

    [Theory]
    [InlineData("SHA-256")]
    [InlineData("SHA2-256")]
    public void Named_crypto_support_matches_the_runtime_without_enum_normalization(string name)
    {
        var algorithm = new HashAlgorithmName(name);
        Assert.False(TeeHashAlgorithmAdapter.TryToTeeHashAlgorithm(algorithm, out _));
        byte[] payload = [7, 8, 9];
        using var destination = new MemoryStream();
        IncrementalHash runtimeHash;
        try
        {
            runtimeHash = IncrementalHash.CreateHash(algorithm);
        }
        catch (CryptographicException)
        {
            Assert.Throws<CryptographicException>(() => new TeeHashStream(algorithm, out _, destination));
            Assert.True(destination.CanWrite);
            return;
        }

        using (runtimeHash)
        {
            runtimeHash.AppendData(payload);
            TeeHashResults results;
            using (var stream = new TeeHashStream(algorithm, out results, destination))
            {
                stream.Write(payload);
            }

            Assert.Equal(name, results[algorithm].Algorithm.Name);
            Assert.Equal(runtimeHash.GetHashAndReset(), results[algorithm].Bytes.ToArray());
        }
    }

    [Fact]
    public void Unnamed_and_undefined_identifiers_cannot_be_published()
    {
        Assert.Throws<ArgumentException>(() => new TeeHashAlgorithmId(default(HashAlgorithmName)));
        Assert.Throws<ArgumentException>(() => new TeeHashAlgorithmId(new HashAlgorithmName(" ")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TeeHashAlgorithmId(default(TeeHashAlgorithm)));
        Assert.Throws<ArgumentException>(() => new TeeHashResult(default, [1]));
    }
}
