using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace TeeForge.Tests;

public class HttpRandomAccessStreamTests
{
    private static readonly Uri ResourceUri = new("https://example.test/large.bin");

    [Fact]
    public async Task Concurrent_reads_use_exact_ranges_and_do_not_move_position()
    {
        byte[] data = Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray();
        var handler = new RangeHandler(data);
        using var client = new HttpClient(handler);
        await using HttpRandomAccessStream stream = await HttpRandomAccessStream.OpenAsync(client, ResourceUri);
        stream.Position = 17;

        byte[] first = new byte[20];
        byte[] second = new byte[30];
        await Task.WhenAll(
            stream.ReadAtAsync(first, 10).AsTask(),
            stream.ReadAtAsync(second, 100).AsTask());

        Assert.Equal(data[10..30], first);
        Assert.Equal(data[100..130], second);
        Assert.Equal(17, stream.Position);
        Assert.Contains((10L, 29L), handler.RequestedRanges);
        Assert.Contains((100L, 129L), handler.RequestedRanges);
    }

    [Fact]
    public async Task Large_range_stream_starts_with_one_request_and_is_forward_only()
    {
        byte[] data = new byte[5 * 1024 * 1024];
        Random.Shared.NextBytes(data);
        var handler = new RangeHandler(data);
        using var client = new HttpClient(handler);
        await using HttpRandomAccessStream source = await HttpRandomAccessStream.OpenAsync(client, ResourceUri);

        await using Stream range = await source.OpenReadRangeAsync(512, 4 * 1024 * 1024);
        byte[] startup = new byte[12 * 1024];
        await range.ReadExactlyAsync(startup);

        Assert.Equal(data.AsSpan(512, startup.Length).ToArray(), startup);
        Assert.False(range.CanSeek);
        Assert.Equal(2, handler.RequestedRanges.Count);
        Assert.Contains((512L, 512L + (4 * 1024 * 1024) - 1), handler.RequestedRanges);
    }

    [Fact]
    public async Task Probe_retries_a_slowdown_response()
    {
        byte[] data = [1, 2, 3, 4];
        var handler = new RangeHandler(data, slowdownProbeOnce: true);
        using var client = new HttpClient(handler);
        var options = new HttpRandomAccessStreamOptions(retryBaseDelay: TimeSpan.FromMilliseconds(1));

        await using HttpRandomAccessStream stream = await HttpRandomAccessStream.OpenAsync(
            client,
            ResourceUri,
            options);

        Assert.Equal(data.Length, stream.Length);
        Assert.Equal(2, handler.ProbeRequests);
    }

    [Fact]
    public async Task Validator_change_faults_the_open_source()
    {
        byte[] data = [1, 2, 3, 4];
        var handler = new RangeHandler(data, changeAfterProbe: true);
        using var client = new HttpClient(handler);
        await using HttpRandomAccessStream stream = await HttpRandomAccessStream.OpenAsync(client, ResourceUri);

        await Assert.ThrowsAsync<HttpRepresentationChangedException>(
            () => stream.ReadAtAsync(new byte[2], 1).AsTask());
        Assert.Throws<HttpRepresentationChangedException>(() => _ = stream.Length);
    }

    [Fact]
    public async Task Interrupted_body_resumes_only_the_unread_suffix()
    {
        byte[] data = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        var handler = new RangeHandler(data, interruptRangeOnce: true);
        using var client = new HttpClient(handler);
        await using HttpRandomAccessStream stream = await HttpRandomAccessStream.OpenAsync(client, ResourceUri);

        byte[] read = new byte[8];
        Assert.Equal(8, await stream.ReadAtAsync(read, 2));

        Assert.Equal(data[2..10], read);
        Assert.Contains((2L, 9L), handler.RequestedRanges);
        Assert.Contains((4L, 9L), handler.RequestedRanges);
    }

    private sealed class RangeHandler : HttpMessageHandler
    {
        private readonly byte[] _data;
        private readonly bool _slowdownProbeOnce;
        private readonly bool _changeAfterProbe;
        private readonly bool _interruptRangeOnce;
        private int _probeRequests;
        private int _probeCompleted;
        private int _rangeInterrupted;

        internal RangeHandler(
            byte[] data,
            bool slowdownProbeOnce = false,
            bool changeAfterProbe = false,
            bool interruptRangeOnce = false)
        {
            _data = data;
            _slowdownProbeOnce = slowdownProbeOnce;
            _changeAfterProbe = changeAfterProbe;
            _interruptRangeOnce = interruptRangeOnce;
        }

        internal ConcurrentQueue<(long From, long To)> RequestedRanges { get; } = new();

        internal int ProbeRequests => Volatile.Read(ref _probeRequests);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RangeItemHeaderValue item = Assert.Single(request.Headers.Range!.Ranges);
            long from = item.From!.Value;
            long to = item.To!.Value;
            RequestedRanges.Enqueue((from, to));

            bool isProbe = Volatile.Read(ref _probeCompleted) == 0 && from == 0 && to == 0;
            if (isProbe)
            {
                int probe = Interlocked.Increment(ref _probeRequests);
                if (_slowdownProbeOnce && probe == 1)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
                }

                Volatile.Write(ref _probeCompleted, 1);
            }
            else if (_changeAfterProbe)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PreconditionFailed));
            }

            int requestedLength = checked((int)(to - from + 1));
            byte[] body = _data.AsSpan((int)from, requestedLength).ToArray();
            bool interrupt = !isProbe && _interruptRangeOnce &&
                Interlocked.CompareExchange(ref _rangeInterrupted, 1, 0) == 0;
            if (interrupt)
            {
                body = body[..Math.Min(2, body.Length)];
            }

            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(new MemoryStream(body, writable: false)),
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"snapshot-1\"");
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, _data.LongLength);
            response.Content.Headers.ContentLength = requestedLength;
            return Task.FromResult(response);
        }
    }
}
