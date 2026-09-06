using System.IO.Hashing;
using System.Security.Cryptography;
using TeeForge.Broadcasting;

namespace TeeForge.Tests;

public class BroadcastHashStreamTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Mixed_hashes_describe_the_source_once_despite_independent_reader_positions()
    {
        byte[] payload = Enumerable.Range(0, 100_003).Select(static value => (byte)value).ToArray();
        await using var source = new BroadcastStreamTests.ChunkedSource(payload, chunkSize: 11);
        source.Position = 9;
        await using var broadcast = new BroadcastHashStream(
            [TeeHashAlgorithm.SHA256, TeeHashAlgorithm.XxHash3, TeeHashAlgorithm.Crc32],
            out TeeHashResults results,
            source, 3, new BroadcastStreamOptions(bufferSize: 17, pauseWriterThreshold: 127, resumeWriterThreshold: 63));

        Task<byte[]> first = BroadcastStreamTests.ReadAllAsync(broadcast.Readers[0], 1);
        Task<byte[]> second = BroadcastStreamTests.ReadAllAsync(broadcast.Readers[1], 19);
        await broadcast.Readers[2].ReadExactlyAsync(new byte[3]);
        Assert.False(results.IsComplete);
        await broadcast.Readers[2].DisposeAsync();
        byte[][] received = await Task.WhenAll(first, second).WaitAsync(Timeout);
        await broadcast.Completion.WaitAsync(Timeout);

        Assert.All(received, bytes => Assert.Equal(payload[9..], bytes));
        Assert.Equal([TeeHashAlgorithm.SHA256, TeeHashAlgorithm.XxHash3, TeeHashAlgorithm.Crc32], results.Keys);
        Assert.Equal(SHA256.HashData(payload.AsSpan(9)), results[TeeHashAlgorithm.SHA256].Bytes.ToArray());
        Assert.Equal(XxHash3.Hash(payload.AsSpan(9)), results[TeeHashAlgorithm.XxHash3].Bytes.ToArray());
        Assert.Equal(Crc32.Hash(payload.AsSpan(9)), results[TeeHashAlgorithm.Crc32].Bytes.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public async Task Cryptographic_results_publish_at_eof_even_before_readers_drain(int length)
    {
        byte[] payload = Enumerable.Range(0, length).Select(static value => (byte)value).ToArray();
        await using var source = new MemoryStream(payload);
        await using var broadcast = new BroadcastHashStream(
            [HashAlgorithmName.SHA512, HashAlgorithmName.SHA256], out TeeHashResults results, source, 2);
        await broadcast.Completion.WaitAsync(Timeout);

        Assert.True(results.IsComplete);
        Assert.All(broadcast.Readers, reader => Assert.Equal(0, reader.Position));
        Assert.Equal(SHA512.HashData(payload), results[HashAlgorithmName.SHA512].Bytes.ToArray());
        Assert.Equal(SHA256.HashData(payload), results[HashAlgorithmName.SHA256].Bytes.ToArray());
        foreach (Stream reader in broadcast.Readers)
        {
            Assert.Equal(payload, await BroadcastStreamTests.ReadAllAsync(reader, 3));
        }
    }

    [Fact]
    public async Task Source_failure_leaves_hashes_incomplete_even_after_disposal()
    {
        await using var source = new BroadcastStreamTests.FailingSource([1, 2, 3]);
        var broadcast = new BroadcastHashStream(HashAlgorithmName.SHA256, out TeeHashResults results, source, 1);
        await Assert.ThrowsAsync<IOException>(() => broadcast.Completion.WaitAsync(Timeout));
        await broadcast.DisposeAsync();
        Assert.False(results.IsComplete);
        Assert.Empty(results);
    }

    [Fact]
    public async Task Abandoning_the_broadcast_does_not_publish_a_prefix_hash()
    {
        await using var source = new MemoryStream(new byte[100]);
        await using var broadcast = new BroadcastHashStream(TeeHashAlgorithm.XxHash3,
            out TeeHashResults results, source, 1,
            new BroadcastStreamOptions(bufferSize: 4, pauseWriterThreshold: 4, resumeWriterThreshold: 2));
        await broadcast.Readers[0].ReadExactlyAsync(new byte[1]);
        await broadcast.Readers[0].DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => broadcast.Completion.WaitAsync(Timeout));
        Assert.False(results.IsComplete);
        Assert.Empty(results);
    }

    [Fact]
    public async Task Broadcast_cancellation_keeps_results_incomplete()
    {
        await using var source = new BroadcastStreamTests.GatedSource([1]);
        using var cancellation = new CancellationTokenSource();
        await using var broadcast = new BroadcastHashStream(HashAlgorithmName.SHA256,
            out TeeHashResults results, source, 1, cancellationToken: cancellation.Token);
        await source.Entered.Task.WaitAsync(Timeout);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => broadcast.Completion.WaitAsync(Timeout));
        Assert.False(results.IsComplete);
    }

    [Fact]
    public void Invalid_hash_selections_do_not_consume_or_dispose_the_source()
    {
        using var source = new MemoryStream([1, 2]);
        Assert.Throws<ArgumentException>(() => new BroadcastHashStream(Array.Empty<TeeHashAlgorithm>(), out _, source, 1));
        Assert.Throws<ArgumentException>(() => new BroadcastHashStream([TeeHashAlgorithm.SHA256, TeeHashAlgorithm.SHA256], out _, source, 1));
        Assert.Throws<ArgumentException>(() => new BroadcastHashStream(default(TeeHashAlgorithm), out _, source, 1));
        Assert.Throws<ArgumentException>(() => new BroadcastHashStream(default(HashAlgorithmName), out _, source, 1));
        Assert.Throws<CryptographicException>(() => new BroadcastHashStream(
            [HashAlgorithmName.SHA256, new HashAlgorithmName("UnknownBroadcastHash")], out _, source, 1));
        Assert.Equal(0, source.Position);
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task Source_cleanup_failure_preserves_completed_hashes_and_closes_readers()
    {
        var source = new DisposeFailingSource([1, 2, 3]);
        var broadcast = new BroadcastHashStream(HashAlgorithmName.SHA256, out TeeHashResults results, source, 2);
        await broadcast.Completion.WaitAsync(Timeout);
        await Assert.ThrowsAsync<IOException>(() => broadcast.DisposeAsync().AsTask());
        Assert.True(results.IsComplete);
        Assert.Equal(SHA256.HashData(new byte[] { 1, 2, 3 }), results[HashAlgorithmName.SHA256].Bytes.ToArray());
        Assert.All(broadcast.Readers, reader => Assert.False(reader.CanRead));
        Assert.False(source.CanRead);
    }

    private sealed class DisposeFailingSource(byte[] payload) : MemoryStream(payload)
    {
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            throw new IOException("Source cleanup failed.");
        }
    }
}
