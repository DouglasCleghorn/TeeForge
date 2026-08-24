using System.Security.Cryptography;
using TeeForge.Hashing.Internal;

namespace TeeForge.Tests;

public class HashWriteStreamTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(65536)]
    public void Fragmented_writes_match_one_shot_hash(int fragmentSize)
    {
        byte[] payload = CreatePayload(100_003);
        using var hashStream = new HashWriteStream(HashAlgorithmName.SHA256);
        using var stream = new BufferedStream(hashStream);

        for (int offset = 0; offset < payload.Length; offset += fragmentSize)
        {
            int count = Math.Min(fragmentSize, payload.Length - offset);
            stream.Write(payload.AsSpan(offset, count));
        }

        stream.Flush();
        Assert.Equal(SHA256.HashData(payload), hashStream.GetHashAndReset());
    }

    [Fact]
    public void Flush_commits_buffered_data_without_resetting_hash()
    {
        byte[] first = CreatePayload(31);
        byte[] second = CreatePayload(47);
        using var hashStream = new HashWriteStream(HashAlgorithmName.SHA256);
        using var stream = new BufferedStream(hashStream);

        stream.Write(first);
        stream.Flush();
        stream.Write(second);

        stream.Flush();
        Assert.Equal(SHA256.HashData([.. first, .. second]), hashStream.GetHashAndReset());
    }

    [Fact]
    public void GetHashAndReset_starts_a_new_hash()
    {
        byte[] first = CreatePayload(17);
        byte[] second = CreatePayload(23);
        using var hashStream = new HashWriteStream(HashAlgorithmName.SHA256);
        using var stream = new BufferedStream(hashStream);

        stream.Write(first);
        stream.Flush();
        Assert.Equal(SHA256.HashData(first), hashStream.GetHashAndReset());

        stream.Write(second);
        stream.Flush();
        Assert.Equal(SHA256.HashData(second), hashStream.GetHashAndReset());
    }

    [Fact]
    public async Task Pre_canceled_write_does_not_change_hash()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var hashStream = new HashWriteStream(HashAlgorithmName.SHA256);
        using var stream = new BufferedStream(hashStream);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await stream.WriteAsync(new byte[] { 1, 2, 3 }, cancellation.Token));

        stream.Flush();
        Assert.Equal(SHA256.HashData([]), hashStream.GetHashAndReset());
    }

    [Fact]
    public void Large_writes_and_buffer_boundaries_preserve_order()
    {
        byte[] prefix = CreatePayload(13);
        byte[] large = CreatePayload(8192);
        byte[] suffix = CreatePayload(29);
        using var hashStream = new HashWriteStream(HashAlgorithmName.SHA512);
        using var stream = new BufferedStream(hashStream, bufferSize: 64);

        stream.Write(prefix);
        stream.Write(large);
        stream.Write(suffix);

        stream.Flush();
        Assert.Equal(SHA512.HashData([.. prefix, .. large, .. suffix]), hashStream.GetHashAndReset());
    }

    [Fact]
    public void BufferedStream_rejects_invalid_buffer_size()
    {
        using var hashStream = new HashWriteStream(HashAlgorithmName.SHA256);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BufferedStream(hashStream, bufferSize: 0));
    }

    [Fact]
    public void Disposed_stream_rejects_operations_and_reports_not_writable()
    {
        var stream = new HashWriteStream(HashAlgorithmName.SHA256);
        stream.Dispose();

        Assert.False(stream.CanWrite);
        Assert.Throws<ObjectDisposedException>(() => stream.Write([1]));
        Assert.Throws<ObjectDisposedException>(() => stream.GetHashAndReset());
    }

    private static byte[] CreatePayload(int length)
    {
        byte[] payload = GC.AllocateUninitializedArray<byte>(length);
        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)((index * 31) ^ (index >> 3));
        }

        return payload;
    }
}
