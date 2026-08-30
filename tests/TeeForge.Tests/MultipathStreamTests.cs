using System.IO.Pipelines;
using TeeForge.Networking;

namespace TeeForge.Tests;

public class MultipathStreamTests
{
    [Fact]
    public async Task Raid1_recombines_one_sequence_and_discards_duplicate_frames()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var options = new MultipathStreamOptions(framePayloadSize: 16);
        await using var sender = new MultipathSenderStream(options);
        await using var receiver = new MultipathReceiverStream(sender.SessionId, options);
        await AddPathAsync(sender, receiver, cancellationToken);
        await AddPathAsync(sender, receiver, cancellationToken);
        byte[] payload = Enumerable.Range(0, 113).Select(static value => (byte)value).ToArray();

        await sender.WriteAsync(payload, cancellationToken);
        await sender.CompleteAsync(cancellationToken);

        Assert.Equal(payload, await ReadToEndAsync(receiver, cancellationToken));
    }

    [Fact]
    public async Task Raid0_supports_path_addition_and_graceful_removal_between_groups()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var options = new MultipathStreamOptions(
            mode: MultipathStreamMode.Raid0,
            framePayloadSize: 8);
        await using var sender = new MultipathSenderStream(options);
        await using var receiver = new MultipathReceiverStream(sender.SessionId, options);
        Guid firstPath = await AddPathAsync(sender, receiver, cancellationToken);
        await AddPathAsync(sender, receiver, cancellationToken);
        byte[] first = Enumerable.Range(0, 41).Select(static value => (byte)value).ToArray();
        byte[] second = Enumerable.Range(80, 37).Select(static value => (byte)value).ToArray();

        await sender.WriteAsync(first, cancellationToken);
        await sender.FlushAsync(cancellationToken);
        Assert.Equal(first, await ReadExactlyAsync(receiver, first.Length, cancellationToken));

        await AddPathAsync(sender, receiver, cancellationToken);
        await sender.WriteAsync(second, cancellationToken);
        await sender.FlushAsync(cancellationToken);
        Assert.Equal(second, await ReadExactlyAsync(receiver, second.Length, cancellationToken));

        Assert.True(await sender.RemovePathAsync(firstPath));
        await sender.CompleteAsync(cancellationToken);
        Assert.Empty(await ReadToEndAsync(receiver, cancellationToken));
    }

    [Fact]
    public async Task Erasure_mode_reconstructs_each_group_from_any_data_shard_count_paths()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var options = new MultipathStreamOptions(
            mode: MultipathStreamMode.ErasureCode,
            framePayloadSize: 16,
            erasureDataShardCount: 3,
            erasureParityShardCount: 2);
        await using var sender = new MultipathSenderStream(options);
        await using var receiver = new MultipathReceiverStream(sender.SessionId, options);
        var pairs = new List<PathPair>();
        for (int index = 0; index < 5; index++)
        {
            PathPair pair = CreatePathPair();
            pairs.Add(pair);
            await sender.AddPathAsync(pair.Sender, cancellationToken);
        }

        foreach (PathPair pair in pairs.Take(3))
        {
            await receiver.AddPathAsync(pair.Receiver, cancellationToken);
        }

        byte[] payload = Enumerable.Range(0, 137).Select(static value => (byte)(value * 13)).ToArray();
        await sender.WriteAsync(payload, cancellationToken);
        await sender.CompleteAsync(cancellationToken);

        Assert.Equal(payload, await ReadToEndAsync(receiver, cancellationToken));
    }

    [Fact]
    public async Task Erasure_mode_falls_back_to_raid1_and_restores_automatically()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var options = new MultipathStreamOptions(
            mode: MultipathStreamMode.ErasureCode,
            framePayloadSize: 8,
            erasureDataShardCount: 3,
            erasureParityShardCount: 2);
        await using var sender = new MultipathSenderStream(options);
        await using var receiver = new MultipathReceiverStream(sender.SessionId, options);
        var pathIds = new List<Guid>();
        pathIds.Add(await AddPathAsync(sender, receiver, cancellationToken));
        pathIds.Add(await AddPathAsync(sender, receiver, cancellationToken));
        Assert.Equal(MultipathStreamMode.Raid1, sender.EffectiveMode);

        byte[] mirrored = [1, 2, 3, 4, 5];
        await sender.WriteAsync(mirrored, cancellationToken);
        await sender.FlushAsync(cancellationToken);
        Assert.Equal(mirrored, await ReadExactlyAsync(receiver, mirrored.Length, cancellationToken));

        for (int index = 0; index < 3; index++)
        {
            pathIds.Add(await AddPathAsync(sender, receiver, cancellationToken));
        }

        Assert.Equal(MultipathStreamMode.ErasureCode, sender.EffectiveMode);
        byte[] encoded = Enumerable.Range(30, 31).Select(static value => (byte)value).ToArray();
        await sender.WriteAsync(encoded, cancellationToken);
        await sender.FlushAsync(cancellationToken);
        Assert.Equal(encoded, await ReadExactlyAsync(receiver, encoded.Length, cancellationToken));

        Assert.True(await sender.RemovePathAsync(pathIds[0]));
        Assert.Equal(MultipathStreamMode.Raid1, sender.EffectiveMode);
        await sender.CompleteAsync(cancellationToken);
        Assert.Empty(await ReadToEndAsync(receiver, cancellationToken));
    }

    [Fact]
    public async Task A_write_waiting_without_paths_resumes_after_a_path_joins()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var options = new MultipathStreamOptions(framePayloadSize: 4);
        await using var sender = new MultipathSenderStream(options);
        await using var receiver = new MultipathReceiverStream(sender.SessionId, options);
        byte[] payload = [9, 8, 7, 6];
        Task write = sender.WriteAsync(payload, cancellationToken).AsTask();
        Assert.False(write.IsCompleted);

        await AddPathAsync(sender, receiver, cancellationToken);
        await write;
        await sender.CompleteAsync(cancellationToken);

        Assert.Equal(payload, await ReadToEndAsync(receiver, cancellationToken));
    }

    [Fact]
    public async Task Explicit_mode_changes_preserve_the_sequence_at_group_boundaries()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var options = new MultipathStreamOptions(framePayloadSize: 8);
        await using var sender = new MultipathSenderStream(options);
        await using var receiver = new MultipathReceiverStream(sender.SessionId, options);
        for (int index = 0; index < 5; index++)
        {
            await AddPathAsync(sender, receiver, cancellationToken);
        }

        byte[] mirrored = [1, 2, 3, 4, 5];
        byte[] striped = Enumerable.Range(20, 19).Select(static value => (byte)value).ToArray();
        byte[] encoded = Enumerable.Range(80, 29).Select(static value => (byte)value).ToArray();
        await sender.WriteAsync(mirrored, cancellationToken);
        await sender.ChangeModeAsync(MultipathStreamMode.Raid0, cancellationToken: cancellationToken);
        await sender.WriteAsync(striped, cancellationToken);
        await sender.ChangeModeAsync(
            MultipathStreamMode.ErasureCode,
            3,
            2,
            cancellationToken);
        await sender.WriteAsync(encoded, cancellationToken);
        await sender.CompleteAsync(cancellationToken);

        byte[] expected = [.. mirrored, .. striped, .. encoded];
        Assert.Equal(expected, await ReadToEndAsync(receiver, cancellationToken));
        Assert.Equal(MultipathStreamMode.ErasureCode, sender.DesiredMode);
        Assert.Equal(MultipathStreamMode.ErasureCode, sender.EffectiveMode);
    }

    [Fact]
    public async Task Path_initializers_run_before_the_multipath_hello()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var options = new MultipathStreamOptions(framePayloadSize: 8);
        await using var sender = new MultipathSenderStream(options);
        await using var receiver = new MultipathReceiverStream(sender.SessionId, options);
        PathPair pair = CreatePathPair();
        byte[] advertisement = "custom-transport"u8.ToArray();

        Guid senderPathId = await sender.AddPathAsync(
            pair.Sender,
            async (stream, token) => await stream.WriteAsync(advertisement, token),
            cancellationToken);
        Guid receiverPathId = await receiver.AddPathAsync(
            pair.Receiver,
            async (stream, token) =>
            {
                byte[] received = new byte[advertisement.Length];
                await stream.ReadExactlyAsync(received, token);
                Assert.Equal(advertisement, received);
            },
            cancellationToken);

        Assert.Equal(senderPathId, receiverPathId);
        byte[] payload = [4, 3, 2, 1];
        await sender.WriteAsync(payload, cancellationToken);
        await sender.CompleteAsync(cancellationToken);
        Assert.Equal(payload, await ReadToEndAsync(receiver, cancellationToken));
    }

    [Fact]
    public async Task Unexpected_path_end_faults_an_active_raid0_receiver()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var options = new MultipathStreamOptions(
            MultipathStreamMode.Raid0,
            framePayloadSize: 8,
            leaveOpen: true);
        await using var sender = new MultipathSenderStream(options);
        await using var receiver = new MultipathReceiverStream(sender.SessionId, options);
        PathPair pair = CreatePathPair();
        await sender.AddPathAsync(pair.Sender, cancellationToken);
        await receiver.AddPathAsync(pair.Receiver, cancellationToken);
        byte[] payload = [1, 2, 3, 4];
        await sender.WriteAsync(payload, cancellationToken);
        await sender.FlushAsync(cancellationToken);
        Assert.Equal(payload, await ReadExactlyAsync(receiver, payload.Length, cancellationToken));

        await pair.Sender.DisposeAsync();

        await Assert.ThrowsAsync<IOException>(async () =>
            await receiver.ReadExactlyAsync(new byte[1], cancellationToken));
        await pair.Receiver.DisposeAsync();
    }

    [Fact]
    public async Task Control_channel_round_trips_health_mode_and_endpoint_messages()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        PathPair pair = CreatePathPair();
        await using var outgoing = new MultipathControlChannel(pair.Sender);
        await using var incoming = new MultipathControlChannel(pair.Receiver);
        Guid pathId = Guid.NewGuid();
        MultipathControlMessage[] messages =
        [
            MultipathControlMessage.CreateReliablePath(pathId),
            MultipathControlMessage.CreateModeChangeRequest(MultipathStreamMode.ErasureCode, 5, 2),
            MultipathControlMessage.CreateEndpointAdvertisement("quic", "edge.example:443"u8.ToArray()),
        ];

        foreach (MultipathControlMessage message in messages)
        {
            await outgoing.SendAsync(message, cancellationToken);
        }

        MultipathControlMessage health = Assert.IsType<MultipathControlMessage>(
            await incoming.ReceiveAsync(cancellationToken));
        MultipathControlMessage mode = Assert.IsType<MultipathControlMessage>(
            await incoming.ReceiveAsync(cancellationToken));
        MultipathControlMessage endpoint = Assert.IsType<MultipathControlMessage>(
            await incoming.ReceiveAsync(cancellationToken));

        Assert.Equal(MultipathControlMessageKind.ReliablePath, health.Kind);
        Assert.Equal(pathId, health.PathId);
        Assert.Equal(MultipathStreamMode.ErasureCode, mode.Mode);
        Assert.Equal(5, mode.DataShardCount);
        Assert.Equal(2, mode.ParityShardCount);
        Assert.Equal("quic", endpoint.EndpointScheme);
        Assert.Equal("edge.example:443"u8.ToArray(), endpoint.EndpointData.ToArray());
    }

    private static async ValueTask<Guid> AddPathAsync(
        MultipathSenderStream sender,
        MultipathReceiverStream receiver,
        CancellationToken cancellationToken)
    {
        PathPair pair = CreatePathPair();
        Guid senderPathId = await sender.AddPathAsync(pair.Sender, cancellationToken);
        Guid receiverPathId = await receiver.AddPathAsync(pair.Receiver, cancellationToken);
        Assert.Equal(senderPathId, receiverPathId);
        return senderPathId;
    }

    private static PathPair CreatePathPair()
    {
        var pipe = new Pipe();
        return new PathPair(
            pipe.Writer.AsStream(leaveOpen: false),
            pipe.Reader.AsStream(leaveOpen: false));
    }

    private static async ValueTask<byte[]> ReadToEndAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, cancellationToken);
        return output.ToArray();
    }

    private static async ValueTask<byte[]> ReadExactlyAsync(
        Stream stream,
        int length,
        CancellationToken cancellationToken)
    {
        byte[] output = new byte[length];
        await stream.ReadExactlyAsync(output, cancellationToken);
        return output;
    }

    private readonly record struct PathPair(Stream Sender, Stream Receiver);
}
