namespace TeeForge.Quickstart;

// Both parsers can mix synchronous and asynchronous I/O. Include the probe and any retries.
internal sealed class RangeRequestCounter() : DelegatingHandler(new HttpClientHandler())
{
    private long _requests;
    private long _requestedBytes;

    public void PrintTotals() => Console.Error.WriteLine(
        FormattableString.Invariant($"HTTP requests {Interlocked.Read(ref _requests)}, requested range bytes {Interlocked.Read(ref _requestedBytes)}"));

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Count(request);
        return base.Send(request, cancellationToken);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Count(request);
        return base.SendAsync(request, cancellationToken);
    }

    private void Count(HttpRequestMessage request)
    {
        Interlocked.Increment(ref _requests);
        if (request.Headers.Range is { } header)
        {
            foreach (var range in header.Ranges)
            {
                if (range.From is long first && range.To is long last)
                    Interlocked.Add(ref _requestedBytes, last - first + 1);
            }
        }
    }
}
