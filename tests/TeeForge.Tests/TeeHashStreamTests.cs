using System.IO.Hashing;
using System.Security.Cryptography;

namespace TeeForge.Tests;

public class TeeHashStreamTests
{
    [Fact]
    public void Explicit_sha256_is_empty_until_disposal_then_published()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        using var destination = new MemoryStream();
        var stream = new TeeHashStream(HashAlgorithmName.SHA256, out TeeHashResults results, destination);

        Assert.IsAssignableFrom<TeeBufferedStream>(stream);
        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.True(stream.CanWrite);
        AssertPending(results);

        stream.Write(payload.AsSpan(0, 2));
        stream.Write(payload.AsSpan(2));
        stream.Flush();
        AssertPending(results);

        stream.Dispose();

        TeeHashResult result = Assert.Single(results).Value;
        byte[] expected = SHA256.HashData(payload);
        Assert.True(results.IsComplete);
        Assert.Equal(HashAlgorithmName.SHA256, result.Algorithm);
        Assert.Equal(expected, result.Bytes.ToArray());
        Assert.Equal(Convert.ToHexString(expected), result.Hex);
        Assert.Equal(Convert.ToBase64String(expected), result.Base64);
    }

    [Fact]
    public async Task Multiple_algorithms_preserve_order_and_async_disposal_honors_leave_open()
    {
        byte[] payload = Enumerable.Range(0, 100_003).Select(static value => (byte)value).ToArray();
        await using var first = new MemoryStream();
        await using var second = new MemoryStream();
        var stream = new TeeHashStream(
            [HashAlgorithmName.SHA512, HashAlgorithmName.SHA256],
            out TeeHashResults results,
            [first, second],
            new TeeBufferedStreamOptions(leaveOpen: true, bufferSize: 127));

        await stream.WriteAsync(payload);
        await stream.DisposeAsync();

        Assert.True(results.IsComplete);
        Assert.Equal([HashAlgorithmName.SHA512, HashAlgorithmName.SHA256], results.Keys);
        Assert.Equal(SHA512.HashData(payload), results[HashAlgorithmName.SHA512].Bytes.ToArray());
        Assert.Equal(SHA256.HashData(payload), results[HashAlgorithmName.SHA256].Bytes.ToArray());
        Assert.Equal(payload, first.ToArray());
        Assert.Equal(payload, second.ToArray());
        Assert.True(first.CanWrite);
        Assert.True(second.CanWrite);
    }

    [Fact]
    public void Cryptographic_and_non_cryptographic_algorithms_can_be_mixed()
    {
        byte[] payload = Enumerable.Range(0, 10_003).Select(static value => (byte)value).ToArray();
        using var destination = new MemoryStream();
        var stream = new TeeHashStream(
            [TeeHashAlgorithm.SHA256, TeeHashAlgorithm.XxHash3, TeeHashAlgorithm.Crc32],
            out TeeHashResults<TeeHashAlgorithm> results,
            [destination],
            new TeeBufferedStreamOptions(leaveOpen: true, bufferSize: 127));

        AssertPending(results);
        stream.Write(payload.AsSpan(0, 13));
        stream.Write(payload.AsSpan(13));
        stream.Dispose();

        Assert.True(results.IsComplete);
        Assert.Equal(
            [TeeHashAlgorithm.SHA256, TeeHashAlgorithm.XxHash3, TeeHashAlgorithm.Crc32],
            results.Keys);
        Assert.Equal(SHA256.HashData(payload), results[TeeHashAlgorithm.SHA256].Bytes.ToArray());
        Assert.Equal(
            ComputeNonCryptographicHash(TeeHashAlgorithm.XxHash3, payload),
            results[TeeHashAlgorithm.XxHash3].Bytes.ToArray());
        Assert.Equal(
            ComputeNonCryptographicHash(TeeHashAlgorithm.Crc32, payload),
            results[TeeHashAlgorithm.Crc32].Bytes.ToArray());
        Assert.Equal(payload, destination.ToArray());
        Assert.True(destination.CanWrite);
    }

    [Theory]
    [InlineData(TeeHashAlgorithm.Crc32)]
    [InlineData(TeeHashAlgorithm.Crc64)]
    [InlineData(TeeHashAlgorithm.XxHash32)]
    [InlineData(TeeHashAlgorithm.XxHash64)]
    [InlineData(TeeHashAlgorithm.XxHash3)]
    [InlineData(TeeHashAlgorithm.XxHash128)]
    public void Every_non_cryptographic_algorithm_matches_System_IO_Hashing(
        TeeHashAlgorithm algorithm)
    {
        byte[] payload = Enumerable.Range(0, 4099).Select(static value => (byte)value).ToArray();
        using var destination = new MemoryStream();
        var stream = new TeeHashStream(algorithm, out TeeHashResults<TeeHashAlgorithm> results, destination);

        stream.Write(payload.AsSpan(0, 17));
        stream.Write(payload.AsSpan(17));
        stream.Dispose();

        Assert.Equal(
            ComputeNonCryptographicHash(algorithm, payload),
            results[algorithm].Bytes.ToArray());
    }

    [Fact]
    public void Empty_input_publishes_the_standard_empty_digest()
    {
        using var destination = new MemoryStream();
        var stream = new TeeHashStream(HashAlgorithmName.SHA256, out TeeHashResults results, destination);

        stream.Dispose();

        Assert.True(results.IsComplete);
        Assert.Equal(SHA256.HashData([]), results[HashAlgorithmName.SHA256].Bytes.ToArray());
    }

    [Fact]
    public void Buffered_retries_are_observed_again_by_the_hash_destination()
    {
        byte[] payload = [1, 2, 3];
        using var failing = new FailFirstWriteStream();
        using var successful = new MemoryStream();
        var stream = new TeeHashStream(
            [HashAlgorithmName.SHA256],
            out TeeHashResults results,
            [failing, successful],
            new TeeBufferedStreamOptions(leaveOpen: true, bufferSize: 16));

        stream.Write(payload);
        Assert.Throws<IOException>(() => stream.Flush());
        stream.Flush();
        stream.Dispose();

        byte[] observedTwice = [.. payload, .. payload];
        Assert.Equal(SHA256.HashData(observedTwice), results[HashAlgorithmName.SHA256].Bytes.ToArray());
        Assert.Equal(observedTwice, successful.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_disposal_failure_does_not_prevent_hash_publication(bool asynchronously)
    {
        byte[] payload = [7, 8, 9];
        using var successful = new MemoryStream();
        var failing = new DisposeThrowingStream();
        var stream = new TeeHashStream(
            HashAlgorithmName.SHA256,
            out TeeHashResults results,
            successful,
            failing);

        stream.Write(payload);
        if (asynchronously)
        {
            await Assert.ThrowsAsync<IOException>(async () => await stream.DisposeAsync());
        }
        else
        {
            Assert.Throws<IOException>(() => stream.Dispose());
        }

        Assert.True(results.IsComplete);
        Assert.Equal(SHA256.HashData(payload), results[HashAlgorithmName.SHA256].Bytes.ToArray());
    }

    [Fact]
    public void Constructor_rejects_invalid_destinations_without_taking_ownership()
    {
        using var writable = new MemoryStream();
        using var readOnly = new MemoryStream([1, 2, 3], writable: false);

        Assert.Throws<ArgumentException>(
            () => new TeeHashStream(HashAlgorithmName.SHA256, out _, Array.Empty<Stream>()));
        Assert.Throws<ArgumentException>(
            () => new TeeHashStream(HashAlgorithmName.SHA256, out _, writable, null!));
        Assert.Throws<ArgumentException>(
            () => new TeeHashStream(HashAlgorithmName.SHA256, out _, writable, writable));
        Assert.Throws<ArgumentException>(
            () => new TeeHashStream(HashAlgorithmName.SHA256, out _, readOnly));

        Assert.True(writable.CanWrite);
        Assert.True(readOnly.CanRead);
    }

    [Fact]
    public void Constructor_rejects_invalid_algorithms_without_taking_ownership()
    {
        using var destination = new MemoryStream();

        Assert.Throws<ArgumentException>(
            () => new TeeHashStream(Array.Empty<HashAlgorithmName>(), out _, [destination]));
        Assert.Throws<ArgumentException>(
            () => new TeeHashStream(
                [HashAlgorithmName.SHA256, HashAlgorithmName.SHA256],
                out _,
                [destination]));
        Assert.Throws<ArgumentException>(
            () => new TeeHashStream([default(HashAlgorithmName)], out _, [destination]));
        Assert.Throws<CryptographicException>(
            () => new TeeHashStream(
                [new HashAlgorithmName("TeeForge.Unsupported.Hash")],
                out _,
                [destination]));

        Assert.True(destination.CanWrite);
    }

    [Fact]
    public void Constructor_rejects_invalid_TeeHashAlgorithms_without_taking_ownership()
    {
        using var destination = new MemoryStream();

        Assert.Throws<ArgumentException>(
            () => new TeeHashStream(Array.Empty<TeeHashAlgorithm>(), out _, [destination]));
        Assert.Throws<ArgumentException>(
            () => new TeeHashStream(
                [TeeHashAlgorithm.XxHash3, TeeHashAlgorithm.XxHash3],
                out _,
                [destination]));
        Assert.Throws<ArgumentException>(
            () => new TeeHashStream([default(TeeHashAlgorithm)], out _, [destination]));

        Assert.True(destination.CanWrite);
    }

    [Fact]
    public void Hash_algorithm_adapters_convert_only_standard_cryptographic_identifiers()
    {
        (TeeHashAlgorithm Tee, HashAlgorithmName DotNet)[] pairs =
        [
            (TeeHashAlgorithm.MD5, HashAlgorithmName.MD5),
            (TeeHashAlgorithm.SHA1, HashAlgorithmName.SHA1),
            (TeeHashAlgorithm.SHA256, HashAlgorithmName.SHA256),
            (TeeHashAlgorithm.SHA384, HashAlgorithmName.SHA384),
            (TeeHashAlgorithm.SHA512, HashAlgorithmName.SHA512),
            (TeeHashAlgorithm.SHA3_256, HashAlgorithmName.SHA3_256),
            (TeeHashAlgorithm.SHA3_384, HashAlgorithmName.SHA3_384),
            (TeeHashAlgorithm.SHA3_512, HashAlgorithmName.SHA3_512),
        ];

        foreach ((TeeHashAlgorithm tee, HashAlgorithmName dotNet) in pairs)
        {
            Assert.Equal(tee, TeeHashAlgorithmAdapter.ToTeeHashAlgorithm(dotNet));
            Assert.True(TeeHashAlgorithmAdapter.TryToTeeHashAlgorithm(dotNet, out TeeHashAlgorithm converted));
            Assert.Equal(tee, converted);
            Assert.True(TeeHashAlgorithmAdapter.TryToHashAlgorithmName(tee, out HashAlgorithmName convertedBack));
            Assert.Equal(dotNet, convertedBack);
        }

        Assert.False(
            TeeHashAlgorithmAdapter.TryToHashAlgorithmName(
                TeeHashAlgorithm.XxHash3,
                out HashAlgorithmName nonCryptographic));
        Assert.Null(nonCryptographic.Name);
        Assert.False(
            TeeHashAlgorithmAdapter.TryToTeeHashAlgorithm(
                new HashAlgorithmName("sha256"),
                out _));
        Assert.Throws<ArgumentException>(
            () => TeeHashAlgorithmAdapter.ToTeeHashAlgorithm(
                new HashAlgorithmName("TeeForge.Unsupported.Hash")));
    }

    [Fact]
    public void Public_hash_result_copies_input_and_exposes_stable_encodings()
    {
        byte[] bytes = [0x01, 0xAB, 0xFF];
        var result = new TeeHashResult(HashAlgorithmName.SHA256, bytes);

        bytes[0] = 0;

        Assert.Equal([0x01, 0xAB, 0xFF], result.Bytes.ToArray());
        Assert.Equal("01ABFF", result.Hex);
        Assert.Equal("Aav/", result.Base64);
        Assert.Same(result.Hex, result.Hex);
        Assert.Same(result.Base64, result.Base64);
    }

    [Fact]
    public void Public_generic_hash_result_copies_input_and_exposes_stable_encodings()
    {
        byte[] bytes = [0x01, 0xAB, 0xFF];
        var result = new TeeHashResult<TeeHashAlgorithm>(TeeHashAlgorithm.XxHash3, bytes);

        bytes[0] = 0;

        Assert.Equal(TeeHashAlgorithm.XxHash3, result.Algorithm);
        Assert.Equal([0x01, 0xAB, 0xFF], result.Bytes.ToArray());
        Assert.Equal("01ABFF", result.Hex);
        Assert.Equal("Aav/", result.Base64);
        Assert.Same(result.Hex, result.Hex);
        Assert.Same(result.Base64, result.Base64);
    }

    [Fact]
    public void Results_publish_once_as_a_complete_read_only_dictionary()
    {
        var results = new TeeHashResults();
        var sha256 = new TeeHashResult(HashAlgorithmName.SHA256, [1]);
        var sha512 = new TeeHashResult(HashAlgorithmName.SHA512, [2]);

        AssertPending(results);

        results.Publish([sha512, sha256]);

        Assert.True(results.IsComplete);
        Assert.Equal(2, results.Count);
        Assert.Equal([HashAlgorithmName.SHA512, HashAlgorithmName.SHA256], results.Keys);
        Assert.Same(sha256, results[HashAlgorithmName.SHA256]);
        Assert.True(results.ContainsKey(HashAlgorithmName.SHA512));
        Assert.True(results.TryGetValue(HashAlgorithmName.SHA512, out TeeHashResult? found));
        Assert.Same(sha512, found);
        Assert.Throws<InvalidOperationException>(() => results.Publish([sha256]));
    }

    private static void AssertPending(TeeHashResults results)
    {
        Assert.False(results.IsComplete);
        Assert.Empty(results);
        Assert.Empty(results.Keys);
        Assert.Empty(results.Values);
        Assert.False(results.ContainsKey(HashAlgorithmName.SHA256));
        Assert.False(results.TryGetValue(HashAlgorithmName.SHA256, out _));
        Assert.Throws<KeyNotFoundException>(() => results[HashAlgorithmName.SHA256]);
    }

    private static void AssertPending(TeeHashResults<TeeHashAlgorithm> results)
    {
        Assert.False(results.IsComplete);
        Assert.Empty(results);
        Assert.Empty(results.Keys);
        Assert.Empty(results.Values);
        Assert.False(results.ContainsKey(TeeHashAlgorithm.SHA256));
        Assert.False(results.TryGetValue(TeeHashAlgorithm.SHA256, out _));
        Assert.Throws<KeyNotFoundException>(() => results[TeeHashAlgorithm.SHA256]);
    }

    private static byte[] ComputeNonCryptographicHash(TeeHashAlgorithm algorithm, ReadOnlySpan<byte> payload)
    {
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

        hash.Append(payload);
        return hash.GetHashAndReset();
    }

    private sealed class FailFirstWriteStream : MemoryStream
    {
        private int _writeAttempts;

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Interlocked.Increment(ref _writeAttempts) == 1)
            {
                throw new IOException("Expected first-write failure.");
            }

            base.Write(buffer, offset, count);
        }
    }

    private sealed class DisposeThrowingStream : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                throw new IOException("Expected disposal failure.");
            }
        }
    }
}
