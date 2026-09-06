using System.IO.Pipelines;
using TeeForge.Networking;
using TeeForge.Networking.Internal;

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
            MultipathControlMessage.CreatePathReceivingValidFrames(pathId),
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

        Assert.Equal(MultipathControlMessageKind.PathReceivingValidFrames, health.Kind);
        Assert.Equal(pathId, health.GetPathReceivingValidFrames());
        Assert.Equal(MultipathStreamMode.ErasureCode, mode.GetModeChangeRequest().Mode);
        Assert.Equal(5, mode.GetModeChangeRequest().DataShardCount);
        Assert.Equal(2, mode.GetModeChangeRequest().ParityShardCount);
        Assert.Equal("quic", endpoint.GetEndpointAdvertisement().Scheme);
        Assert.Equal("edge.example:443"u8.ToArray(), endpoint.GetEndpointAdvertisement().Data.ToArray());
    }

    [Fact]
    public async Task Receiver_timeouts_do_not_consume_future_frames()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid sessionId = Guid.NewGuid();
        var options = new MultipathStreamOptions(pathAvailabilityTimeout: TimeSpan.FromMilliseconds(30));
        await using var receiver = new MultipathReceiverStream(sessionId, options);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            IOException error = await Assert.ThrowsAsync<IOException>(async () =>
                await receiver.ReadExactlyAsync(new byte[4], cancellationToken));
            Assert.IsType<TimeoutException>(error.InnerException);
        }

        using MemoryStream path = CreateFramedPath(sessionId, [1, 2, 3, 4]);
        await receiver.AddPathAsync(path, cancellationToken);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await ReadToEndAsync(receiver, cancellationToken));
    }

    [Fact]
    public async Task Receiver_cancellation_preserves_data_for_the_next_read()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid sessionId = Guid.NewGuid();
        await using var receiver = new MultipathReceiverStream(sessionId, new MultipathStreamOptions());
        using var readSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<int> read = receiver.ReadAsync(new byte[4], readSource.Token).AsTask();
        await readSource.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        using MemoryStream path = CreateFramedPath(sessionId, [4, 3, 2, 1]);
        await receiver.AddPathAsync(path, cancellationToken);
        Assert.Equal(new byte[] { 4, 3, 2, 1 }, await ReadToEndAsync(receiver, cancellationToken));
    }

    [Fact]
    public async Task Joining_an_idle_path_ends_the_availability_timeout()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var options = new MultipathStreamOptions(framePayloadSize: 4,
            pathAvailabilityTimeout: TimeSpan.FromMilliseconds(100));
        await using var sender = new MultipathSenderStream(options);
        await using var receiver = new MultipathReceiverStream(sender.SessionId, options);
        byte[] output = new byte[4];
        Task<int> read = receiver.ReadAsync(output, cancellationToken).AsTask();
        await AddPathAsync(sender, receiver, cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        Assert.False(read.IsCompleted);
        await sender.WriteAsync(new byte[] { 1, 2, 3, 4 }, cancellationToken);
        Assert.Equal(4, await read.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, output);
    }

    [Fact]
    public async Task Removing_the_last_idle_path_starts_the_availability_timeout()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var options = new MultipathStreamOptions(pathAvailabilityTimeout: TimeSpan.FromMilliseconds(30));
        await using var sender = new MultipathSenderStream(options);
        await using var receiver = new MultipathReceiverStream(sender.SessionId, options);
        Guid pathId = await AddPathAsync(sender, receiver, cancellationToken);
        Task<int> read = receiver.ReadAsync(new byte[1], cancellationToken).AsTask();
        Assert.True(await receiver.RemovePathAsync(pathId));
        await Assert.ThrowsAsync<IOException>(() => read.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Receiver_disposal_unblocks_an_idle_logical_read(bool leaveOpen)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var receiver = new MultipathReceiverStream(new MultipathStreamOptions(leaveOpen: leaveOpen));
        Task<int> read = receiver.ReadAsync(new byte[1], cancellationToken).AsTask();
        await receiver.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => read.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
    }

    [Fact]
    public async Task Full_receive_queue_stops_parsing_and_resumes_without_losing_frames()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid sessionId = Guid.NewGuid();
        byte[] payload = Enumerable.Range(1, 40).Select(static x => (byte)x).ToArray();
        using MemoryStream path = CreateFramedPath(sessionId, payload);
        var options = new MultipathStreamOptions(receiveQueueCapacity: 1, leaveOpen: true);
        await using var receiver = new MultipathReceiverStream(sessionId, options);
        await receiver.AddPathAsync(path, cancellationToken);

        // MemoryStream reads complete synchronously. The pump must stop after one queued frame
        // and one staged frame; an unbounded queue consumes the entire path before Add returns.
        Assert.Equal(44 + 2 * (64 + 4), path.Position);
        Assert.True(path.Position < path.Length);
        Assert.Equal(payload, await ReadToEndAsync(receiver, cancellationToken));
    }

    [Fact]
    public async Task Full_receive_queue_does_not_drop_a_path_failure()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid sessionId = Guid.NewGuid();
        using MemoryStream path = CreateFramedPath(sessionId, [1, 2, 3, 4], MultipathStreamMode.Raid0, complete: false);
        await using var receiver = new MultipathReceiverStream(sessionId,
            new MultipathStreamOptions(receiveQueueCapacity: 1));
        await receiver.AddPathAsync(path, cancellationToken);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await ReadExactlyAsync(receiver, 4, cancellationToken));
        await Assert.ThrowsAsync<IOException>(async () =>
            await receiver.ReadAsync(new byte[1], cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
    }

    [Fact]
    public async Task Receiver_rejects_a_group_over_its_byte_budget()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid sessionId = Guid.NewGuid();
        using MemoryStream path = CreateFramedPath(sessionId, [1, 2, 3, 4]);
        await using var receiver = new MultipathReceiverStream(sessionId,
            new MultipathStreamOptions(maximumReorderBytes: 3));
        await receiver.AddPathAsync(path, cancellationToken);
        IOException error = await Assert.ThrowsAsync<IOException>(async () =>
            await receiver.ReadExactlyAsync(new byte[4], cancellationToken));
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.False(receiver.CanRead);
    }

    [Fact]
    public async Task Receiver_releases_the_group_budget_after_consumption()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid sessionId = Guid.NewGuid();
        byte[] payload = Enumerable.Range(1, 40).Select(static x => (byte)x).ToArray();
        using MemoryStream path = CreateFramedPath(sessionId, payload);
        await using var receiver = new MultipathReceiverStream(sessionId,
            new MultipathStreamOptions(maximumReorderBytes: 4));
        await receiver.AddPathAsync(path, cancellationToken);
        Assert.Equal(payload, await ReadToEndAsync(receiver, cancellationToken));
    }

    [Fact]
    public async Task Receive_frame_limit_is_checked_before_reading_the_body()
    {
        Guid sessionId = Guid.NewGuid();
        byte[] frame = MultipathProtocol.CreateDataFrame(sessionId, 1, 0, MultipathStreamMode.Raid1,
            0, 1, 0, 4, new byte[4]);
        using var path = new MemoryStream(frame);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await MultipathProtocol.ReadDataOrCompleteAsync(path, sessionId, Guid.NewGuid(),
                TestContext.Current.CancellationToken, maximumPayloadSize: 3));
        Assert.Equal(4, path.Position);
    }

    [Fact]
    public async Task Receive_shard_limit_rejects_excessive_geometry()
    {
        Guid sessionId = Guid.NewGuid();
        byte[] frame = MultipathProtocol.CreateDataFrame(sessionId, 1, 0, MultipathStreamMode.ErasureCode,
            0, 2, 1, 4, new byte[4]);
        using var path = new MemoryStream(frame);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await MultipathProtocol.ReadDataOrCompleteAsync(path, sessionId, Guid.NewGuid(),
                TestContext.Current.CancellationToken, maximumShardCount: 2));
    }

    [Fact]
    public async Task Status_distinguishes_configuration_capacity_and_lifecycle()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var options = new MultipathStreamOptions(MultipathStreamMode.ErasureCode,
            erasureDataShardCount: 2, erasureParityShardCount: 1);
        await using var sender = new MultipathSenderStream(options);
        MultipathSenderStatus initial = sender.Status;
        Assert.Equal(MultipathProtectionState.Unavailable, initial.Protection);
        Assert.Equal(MultipathSenderState.Open, initial.State);
        using var first = new MemoryStream();
        using var second = new MemoryStream();
        using var third = new MemoryStream();
        await sender.AddPathAsync(first, cancellationToken);
        Assert.Equal(MultipathProtectionState.Unprotected, sender.Status.Protection);
        await sender.AddPathAsync(second, cancellationToken);
        Assert.Equal(MultipathProtectionState.Mirrored, sender.Status.Protection);
        Guid thirdId = await sender.AddPathAsync(third, cancellationToken);
        MultipathSenderStatus encoded = sender.Status;
        Assert.Equal(MultipathProtectionState.ErasureProtected, encoded.Protection);
        Assert.Equal(MultipathStreamMode.ErasureCode, encoded.EffectiveMode);
        Assert.Equal(3, encoded.PathCount);
        Assert.True(encoded.MembershipEpoch > initial.MembershipEpoch);
        Assert.Equal(0, initial.PathCount);
        await sender.RemovePathAsync(thirdId);
        Assert.Equal(MultipathStreamMode.ErasureCode, sender.Status.DesiredMode);
        Assert.Equal(MultipathProtectionState.Mirrored, sender.Status.Protection);
        await sender.ChangeModeAsync(MultipathStreamMode.Raid0, cancellationToken: cancellationToken);
        Assert.Equal(MultipathProtectionState.Unprotected, sender.Status.Protection);
        await sender.CompleteAsync(cancellationToken);
        Assert.Equal(MultipathSenderState.Completed, sender.Status.State);
        Assert.Equal(MultipathProtectionState.Unavailable, sender.Status.Protection);
        await sender.DisposeAsync();
        Assert.Equal(MultipathSenderState.Disposed, sender.Status.State);
    }

    [Fact]
    public void Typed_control_accessors_reject_the_wrong_message_kind()
    {
        Guid pathId = Guid.NewGuid();
        MultipathControlMessage health = MultipathControlMessage.CreatePathReceivingValidFrames(pathId);
        Assert.Equal(pathId, health.GetPathReceivingValidFrames());
        Assert.Throws<InvalidOperationException>(() => health.GetModeChangeRequest());
        Assert.Throws<InvalidOperationException>(() => health.GetEndpointAdvertisement());
        MultipathControlMessage mode = MultipathControlMessage.CreateModeChangeRequest(MultipathStreamMode.ErasureCode, 4, 2);
        MultipathModeChangeRequest request = mode.GetModeChangeRequest();
        Assert.Equal(MultipathStreamMode.ErasureCode, request.Mode);
        Assert.Equal(4, request.DataShardCount);
        Assert.Equal(2, request.ParityShardCount);
        Assert.Throws<InvalidOperationException>(() => mode.GetPathReceivingValidFrames());
        Assert.Throws<InvalidOperationException>(() => mode.GetEndpointAdvertisement());
        byte[] data = [1, 2, 3];
        MultipathControlMessage endpoint = MultipathControlMessage.CreateEndpointAdvertisement("quic", data);
        data[0] = 99;
        MultipathEndpointAdvertisement advertisement = endpoint.GetEndpointAdvertisement();
        Assert.Equal("quic", advertisement.Scheme);
        Assert.Equal(new byte[] { 1, 2, 3 }, advertisement.Data.ToArray());
        Assert.Throws<InvalidOperationException>(() => endpoint.GetPathReceivingValidFrames());
        Assert.Throws<InvalidOperationException>(() => endpoint.GetModeChangeRequest());
        Assert.Equal(MultipathControlProtocol.Encode(health),
            MultipathControlProtocol.Encode(MultipathControlMessage.CreatePathReceivingValidFrames(pathId)));
    }

    [Fact]
    public async Task Concurrent_completion_calls_wait_for_the_same_publication()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var path = new PausedWriteStream();
        await using var sender = new MultipathSenderStream(new MultipathStreamOptions(leaveOpen: true));
        await sender.AddPathAsync(path, cancellationToken);
        path.PauseWrites = true;
        Task first = sender.CompleteAsync(cancellationToken).AsTask();
        await path.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal(MultipathSenderState.Completing, sender.Status.State);
        Task second = sender.CompleteAsync(cancellationToken).AsTask();
        Assert.False(second.IsCompleted);
        path.ResumeWrites.SetResult(true);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal(MultipathSenderState.Completed, sender.Status.State);
        Assert.Equal(44 + 36, path.Length); // One hello and one EOF marker.
    }

    [Theory]
    [InlineData(MultipathStreamMode.Raid1)]
    [InlineData(MultipathStreamMode.Raid0)]
    public async Task Cancelling_group_publication_faults_the_sender_and_removes_the_path(MultipathStreamMode mode)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var path = new PausedWriteStream();
        await using var sender = new MultipathSenderStream(new MultipathStreamOptions(mode, framePayloadSize: 4));
        await sender.AddPathAsync(path, cancellationToken);
        path.PauseWrites = true;
        using var writeSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task write = sender.WriteAsync(new byte[] { 1, 2, 3, 4 }, writeSource.Token).AsTask();
        await path.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await writeSource.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Equal(MultipathSenderState.Faulted, sender.Status.State);
        Assert.Equal(0, sender.Status.PathCount);
        await Assert.ThrowsAsync<IOException>(async () =>
            await sender.WriteAsync(new byte[] { 1 }, cancellationToken));
    }

    [Fact]
    public async Task Erasure_group_budget_includes_missing_shards_and_decoded_bytes()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid sessionId = Guid.NewGuid();
        using var path = new MemoryStream();
        path.Write(MultipathProtocol.CreateHelloFrame(sessionId, Guid.NewGuid()));
        path.Write(MultipathProtocol.CreateDataFrame(sessionId, 1, 0, MultipathStreamMode.ErasureCode,
            0, 2, 1, 8, new byte[4]));
        path.Position = 0;
        await using var receiver = new MultipathReceiverStream(sessionId,
            new MultipathStreamOptions(maximumReorderBytes: 19)); // 3 * 4 + 8 = 20 reserved bytes.
        await receiver.AddPathAsync(path, cancellationToken);
        IOException error = await Assert.ThrowsAsync<IOException>(async () =>
            await receiver.ReadExactlyAsync(new byte[8], cancellationToken));
        Assert.IsType<InvalidDataException>(error.InnerException);
    }

    [Fact]
    public async Task Documented_concurrent_transfer_completes_under_transport_backpressure()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        CancellationToken token = timeout.Token;
        var options = new MultipathStreamOptions(mode: MultipathStreamMode.Raid1,
            framePayloadSize: 16 * 1024, pathAvailabilityTimeout: TimeSpan.FromSeconds(5));
        await using var sender = new MultipathSenderStream(options);
        await using var receiver = new MultipathReceiverStream(sender.SessionId, options);
        for (int index = 0; index < 2; index++)
        {
            var pipe = new Pipe();
            Task<Guid> joinSender = sender.AddPathAsync(pipe.Writer.AsStream(), token).AsTask();
            Task<Guid> joinReceiver = receiver.AddPathAsync(pipe.Reader.AsStream(), token).AsTask();
            await Task.WhenAll(joinSender, joinReceiver);
            Assert.Equal(await joinSender, await joinReceiver);
        }

        byte[] payload = new byte[1024 * 1024];
        new Random(42).NextBytes(payload);
        using var source = new MemoryStream(payload);
        using var destination = new MemoryStream();
        Task receive = receiver.CopyToAsync(destination, token);
        Task send = SendAsync();
        await Task.WhenAll(send, receive);
        Assert.Equal(payload, destination.ToArray());

        async Task SendAsync()
        {
            await source.CopyToAsync(sender, token);
            await sender.CompleteAsync(token);
        }
    }

    private sealed class PausedWriteStream : MemoryStream
    {
        internal bool PauseWrites { get; set; }
        internal TaskCompletionSource<bool> WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ResumeWrites { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (PauseWrites)
            {
                WriteStarted.TrySetResult(true);
                await ResumeWrites.Task.WaitAsync(cancellationToken);
            }
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private static MemoryStream CreateFramedPath(Guid sessionId, byte[] payload,
        MultipathStreamMode mode = MultipathStreamMode.Raid1, bool complete = true)
    {
        var path = new MemoryStream();
        path.Write(MultipathProtocol.CreateHelloFrame(sessionId, Guid.NewGuid()));
        ulong sequence = 0;
        for (int offset = 0; offset < payload.Length; offset += 4)
        {
            int length = Math.Min(4, payload.Length - offset);
            path.Write(MultipathProtocol.CreateDataFrame(sessionId, 1, sequence++, mode,
                0, 1, 0, length, payload.AsSpan(offset, length)));
        }
        if (complete)
        {
            path.Write(MultipathProtocol.CreateCompleteFrame(sessionId, sequence));
        }
        path.Position = 0;
        return path;
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
