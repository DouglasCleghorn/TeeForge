using System.IO.Hashing;
using System.Security.Cryptography;

namespace TeeForge.Tests;

public class HashingReadStreamTests
{
    [Fact]
    public async Task Pull_reads_publish_all_algorithms_only_after_observed_eof()
    {
        byte[] data = [0, 1, 2, 3, 4, 5];
        using var source = new MemoryStream(data) { Position = 1 };
        using var reader = new HashingReadStream(source, [TeeHashAlgorithm.SHA256, TeeHashAlgorithm.Crc32], leaveOpen: true);
        Assert.Equal(1, source.Position);
        Assert.False(reader.CanSeek);
        Assert.False(reader.CanWrite);
        byte[] actual = new byte[5];
        actual[0] = checked((byte)reader.ReadByte());
        Assert.Equal(2, reader.Read(actual, 1, 2));
        Assert.Equal(2, await reader.ReadAsync(actual.AsMemory(3, 2)));
        Assert.Equal(data[1..], actual);
        Assert.Equal(5, reader.BytesRead);
        Assert.False(reader.Results.IsComplete);
        Assert.Equal(0, await reader.ReadAsync(Memory<byte>.Empty));
        Assert.Empty(reader.Results);
        Assert.Equal(-1, reader.ReadByte());
        Assert.True(reader.Results.IsComplete);
        Assert.Equal(SHA256.HashData(actual), reader.Results[HashAlgorithmName.SHA256].Bytes.ToArray());
        Assert.Equal(Crc32.Hash(actual), reader.Results[TeeHashAlgorithm.Crc32].Bytes.ToArray());

        source.Position = 0;
        Assert.Equal(0, await reader.ReadAsync(actual));
        Assert.Equal(0, source.Position);
        reader.Dispose();
        Assert.True(source.CanRead);
        Assert.Equal(5, reader.BytesRead);
        Assert.True(reader.Results.IsComplete);
    }

    [Fact]
    public async Task Empty_source_requires_a_nonempty_read_to_finalize()
    {
        using var source = new MemoryStream();
        await using var reader = new HashingReadStream(source, new[] { HashAlgorithmName.SHA512 });
        Assert.Equal(0, reader.Read(Span<byte>.Empty));
        Assert.False(reader.Results.IsComplete);
        Assert.Equal(0, await reader.ReadAsync(new byte[1]));
        Assert.Equal(0, reader.BytesRead);
        Assert.Equal(SHA512.HashData(Array.Empty<byte>()), reader.Results[HashAlgorithmName.SHA512].Bytes.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Early_disposal_keeps_the_count_without_publishing_partial_hashes(bool leaveOpen)
    {
        using var source = new MemoryStream([1, 2]);
        var reader = new HashingReadStream(source, HashAlgorithmName.SHA256, leaveOpen);
        Assert.Equal(1, reader.ReadByte());
        await reader.DisposeAsync();
        Assert.Equal(1, reader.BytesRead);
        Assert.False(reader.Results.IsComplete);
        Assert.Empty(reader.Results);
        Assert.Equal(leaveOpen, source.CanRead);
        Assert.Throws<ObjectDisposedException>(() => reader.ReadByte());
    }

    [Fact]
    public async Task A_faulting_source_keeps_its_prefix_count_and_never_publishes_results()
    {
        using var source = new BroadcastStreamTests.FailingSource([42]);
        await using var reader = new HashingReadStream(source, TeeHashAlgorithm.SHA256);
        Assert.Equal(1, await reader.ReadAsync(new byte[1]));
        IOException failure = await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync(new byte[1]).AsTask());
        Assert.Same(failure, Assert.Throws<IOException>(() => reader.ReadByte()));
        Assert.Equal(1, reader.BytesRead);
        Assert.False(reader.Results.IsComplete);
    }

    [Fact]
    public async Task Cancellation_while_waiting_to_read_preserves_the_active_read()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var source = new BroadcastStreamTests.GatedSource([42]);
        await using var reader = new HashingReadStream(source, HashAlgorithmName.SHA256);
        byte[] actual = new byte[1];
        Task<int> active = reader.ReadAsync(actual, timeout.Token).AsTask();
        await source.Entered.Task.WaitAsync(timeout.Token);
        using var cancel = new CancellationTokenSource();
        Task<int> waiting = reader.ReadAsync(new byte[1], cancel.Token).AsTask();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        source.Release.SetResult();
        Assert.Equal(1, await active);
        Assert.Equal(42, actual[0]);
        Assert.Equal(0, await reader.ReadAsync(actual));
        Assert.Equal(SHA256.HashData(actual), reader.Results[HashAlgorithmName.SHA256].Bytes.ToArray());
    }

    [Fact]
    public async Task Cancellation_during_source_read_cannot_publish_a_partial_digest()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var source = new BroadcastStreamTests.GatedSource([41, 42]);
        await using var reader = new HashingReadStream(source, TeeHashAlgorithm.SHA256);
        Assert.Equal(41, reader.ReadByte());
        using var cancel = new CancellationTokenSource();
        Task<int> pending = reader.ReadAsync(new byte[1], cancel.Token).AsTask();
        await source.Entered.Task.WaitAsync(timeout.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(timeout.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => reader.ReadByte());
        Assert.Equal(1, reader.BytesRead);
        Assert.False(reader.Results.IsComplete);
    }

    [Fact]
    public async Task Invalid_arguments_and_pre_cancellation_leave_source_untouched()
    {
        using var source = new MemoryStream([42]);
        Assert.Throws<ArgumentException>(() => new HashingReadStream(source, Array.Empty<HashAlgorithmName>()));
        Assert.Throws<ArgumentException>(() => new HashingReadStream(source, [TeeHashAlgorithm.SHA256, TeeHashAlgorithm.SHA256]));
        Assert.Throws<CryptographicException>(() => new HashingReadStream(source, new HashAlgorithmName("UnknownHashingReadAlgorithm")));

        using var reader = new HashingReadStream(source, TeeHashAlgorithm.SHA256);
        Assert.Throws<ArgumentNullException>(() => reader.Read(null!, 0, 0));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(new byte[1], new CancellationToken(true)).AsTask());
        Assert.Equal(0, source.Position);
        Assert.Equal(42, reader.ReadByte());
        Assert.Equal(-1, reader.ReadByte());
        Assert.True(reader.Results.IsComplete);
    }
}
