using System.IO.Hashing;
using System.Security.Cryptography;
using TeeForge.Broadcasting;

namespace TeeForge.Tests;

public class StreamCopyHashExtensionsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(0)]
    [InlineData(10003)]
    public async Task Single_destination_hashes_only_remaining_source_bytes_and_keeps_streams_open(int length)
    {
        byte[] payload = Enumerable.Range(0, length + 9).Select(static value => (byte)value).ToArray();
        await using var source = new BroadcastStreamTests.ChunkedSource(payload, 11);
        source.Position = 9;
        await using var destination = new ObservedDestination();
        destination.WriteByte(42);

        TeeHashResults hashes = await source.CopyToAsync(TeeHashAlgorithm.SHA256, destination).WaitAsync(Timeout);

        Assert.True(hashes.IsComplete);
        Assert.Equal(SHA256.HashData(payload.AsSpan(9)), hashes[TeeHashAlgorithm.SHA256].Bytes.ToArray());
        Assert.Equal(new byte[] { 42 }.Concat(payload[9..]), destination.ToArray());
        Assert.True(source.CanRead);
        Assert.True(destination.CanWrite);
        Assert.Equal(0, destination.FlushCalls);
    }

    [Fact]
    public async Task Mixed_hashes_cover_source_once_and_return_only_after_slow_destination_finishes()
    {
        byte[] payload = Enumerable.Range(0, 128).Select(static value => (byte)value).ToArray();
        await using var source = new MemoryStream(payload);
        await using var slow = new GatedDestination();
        await using var fast = new ObservedDestination(payload.Length);
        Task<TeeHashResults> copy = source.CopyToAsync(
            [TeeHashAlgorithm.SHA256, TeeHashAlgorithm.XxHash3], [slow, fast],
            new BroadcastCopyOptions(bufferSize: 7, pauseWriterThreshold: 1024, resumeWriterThreshold: 512));
        try
        {
            await Task.WhenAll(slow.Entered.Task, fast.Reached.Task).WaitAsync(Timeout);
            Assert.Equal(payload.Length, source.Position);
            Assert.False(copy.IsCompleted);
            Assert.Equal(0, slow.Length);
        }
        finally
        {
            slow.Release.TrySetResult();
        }

        TeeHashResults hashes = await copy.WaitAsync(Timeout);
        Assert.Equal([TeeHashAlgorithm.SHA256, TeeHashAlgorithm.XxHash3], hashes.Keys);
        Assert.Equal(SHA256.HashData(payload), hashes[TeeHashAlgorithm.SHA256].Bytes.ToArray());
        Assert.Equal(XxHash3.Hash(payload), hashes[TeeHashAlgorithm.XxHash3].Bytes.ToArray());
        Assert.Equal(payload, slow.ToArray());
        Assert.Equal(payload, fast.ToArray());
    }

    [Fact]
    public async Task Cryptographic_algorithm_names_support_one_or_many_destinations()
    {
        byte[] payload = [1, 2, 3, 4];
        await using var source = new MemoryStream(payload);
        await using var first = new MemoryStream();
        await using var second = new MemoryStream();
        TeeHashResults single = await source.CopyToAsync(HashAlgorithmName.SHA256, first).WaitAsync(Timeout);
        Assert.Equal(SHA256.HashData(payload), single[HashAlgorithmName.SHA256].Bytes.ToArray());

        source.Position = 0;
        first.SetLength(0);
        TeeHashResults multiple = await source.CopyToAsync(
            new[] { HashAlgorithmName.SHA256, HashAlgorithmName.SHA512 }, first, second).WaitAsync(Timeout);
        Assert.Equal(SHA256.HashData(payload), multiple[HashAlgorithmName.SHA256].Bytes.ToArray());
        Assert.Equal(SHA512.HashData(payload), multiple[HashAlgorithmName.SHA512].Bytes.ToArray());
        Assert.Equal(payload, first.ToArray());
        Assert.Equal(payload, second.ToArray());
    }

    [Fact]
    public async Task Algorithm_and_destination_enumerables_are_snapshotted_once()
    {
        await using var source = new MemoryStream([1, 2, 3]);
        await using var destination = new MemoryStream();
        int algorithmEnumerations = 0;
        int destinationEnumerations = 0;
        TeeHashResults hashes = await source.CopyToAsync(Algorithms(), Destinations()).WaitAsync(Timeout);
        Assert.Equal(1, algorithmEnumerations);
        Assert.Equal(1, destinationEnumerations);
        Assert.Equal(SHA256.HashData(new byte[] { 1, 2, 3 }), hashes[TeeHashAlgorithm.SHA256].Bytes.ToArray());

        IEnumerable<TeeHashAlgorithm> Algorithms()
        {
            Assert.Equal(1, ++algorithmEnumerations);
            yield return TeeHashAlgorithm.SHA256;
        }

        IEnumerable<Stream> Destinations()
        {
            Assert.Equal(1, ++destinationEnumerations);
            yield return destination;
        }
    }

    [Fact]
    public async Task Invalid_hash_selections_fail_before_source_io()
    {
        await using var source = new MemoryStream([1, 2, 3]);
        await using var destination = new MemoryStream();
        Assert.Throws<ArgumentException>(() => { _ = source.CopyToAsync(Array.Empty<TeeHashAlgorithm>(), destination); });
        Assert.Throws<ArgumentException>(() => { _ = source.CopyToAsync([TeeHashAlgorithm.SHA256, TeeHashAlgorithm.SHA256], destination); });
        Assert.Throws<ArgumentException>(() => { _ = source.CopyToAsync((TeeHashAlgorithm)(-1), destination); });
        Assert.Throws<ArgumentException>(() => { _ = source.CopyToAsync(default(HashAlgorithmName), destination); });
        Assert.Throws<ArgumentNullException>(() => { _ = source.CopyToAsync((IEnumerable<HashAlgorithmName>)null!, destination); });
        await Assert.ThrowsAsync<CryptographicException>(() => source.CopyToAsync(new HashAlgorithmName("not-a-hash"), destination));
        Assert.Equal(0, source.Position);
        Assert.Equal(0, destination.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_cancels_hash_result_task(bool beforeCopy)
    {
        await using var source = new MemoryStream(new byte[128]);
        await using var destination = new GatedDestination();
        using var cancellation = new CancellationTokenSource();
        if (beforeCopy) cancellation.Cancel();
        Task<TeeHashResults> copy = source.CopyToAsync(HashAlgorithmName.SHA256, destination, cancellationToken: cancellation.Token);
        if (!beforeCopy)
        {
            await destination.Entered.Task.WaitAsync(Timeout);
            await cancellation.CancelAsync();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy.WaitAsync(Timeout));
        Assert.True(copy.IsCanceled);
        if (beforeCopy) Assert.Equal(0, source.Position);
        Assert.True(source.CanRead);
        Assert.True(destination.CanWrite);
    }

    [Theory]
    [InlineData(BroadcastCopyFailureBehavior.Stop)]
    [InlineData(BroadcastCopyFailureBehavior.Continue)]
    public async Task Destination_failure_faults_hash_task_and_continue_finishes_healthy_copy(BroadcastCopyFailureBehavior behavior)
    {
        byte[] payload = new byte[1003];
        await using var source = new MemoryStream(payload);
        await using var failed = new FailingDestination();
        await using var healthy = new MemoryStream();
        Task<TeeHashResults> copy = source.CopyToAsync(TeeHashAlgorithm.SHA256, [failed, healthy],
            new BroadcastCopyOptions(bufferSize: 7, pauseWriterThreshold: 28, resumeWriterThreshold: 14, failureBehavior: behavior));
        AggregateException aggregate = await Assert.ThrowsAsync<AggregateException>(() => copy.WaitAsync(Timeout));
        Assert.True(copy.IsFaulted);
        var failure = Assert.IsType<BroadcastCopyDestinationException>(Assert.Single(aggregate.InnerExceptions));
        Assert.Equal(0, failure.DestinationIndex);
        Assert.IsType<IOException>(failure.InnerException);
        if (behavior == BroadcastCopyFailureBehavior.Continue) Assert.Equal(payload, healthy.ToArray());
        Assert.True(failed.CanWrite);
        Assert.True(healthy.CanWrite);
    }

    [Fact]
    public async Task Source_failure_faults_hash_task_after_copying_available_prefix()
    {
        await using var source = new BroadcastStreamTests.FailingSource([1, 2, 3]);
        await using var first = new MemoryStream();
        await using var second = new MemoryStream();
        Task<TeeHashResults> copy = source.CopyToAsync(TeeHashAlgorithm.SHA256, first, second);
        AggregateException aggregate = await Assert.ThrowsAsync<AggregateException>(() => copy.WaitAsync(Timeout));
        Assert.IsType<IOException>(Assert.Single(aggregate.InnerExceptions));
        Assert.Equal(new byte[] { 1, 2, 3 }, first.ToArray());
        Assert.Equal(first.ToArray(), second.ToArray());
    }

    private sealed class ObservedDestination(int signalAt = int.MaxValue) : MemoryStream
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int FlushCalls { get; private set; }
        public override void Flush() => FlushCalls++;
        public override Task FlushAsync(CancellationToken cancellationToken) { FlushCalls++; return Task.CompletedTask; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            if (Length >= signalAt) Reached.TrySetResult();
        }
    }

    private sealed class GatedDestination : MemoryStream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class FailingDestination : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Destination failed."));
    }
}
