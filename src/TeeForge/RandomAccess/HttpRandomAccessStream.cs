using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using TeeForge.RandomAccess.Internal;

namespace TeeForge.RandomAccess;

/// <summary>Provides read-only random access to one HTTP resource using validated byte ranges.</summary>
public class HttpRandomAccessStream : Stream, ITeeRandomAccessStream, ITeeRangeReadSource
{
    private readonly HttpClient _client;
    private readonly Uri _requestUri;
    private readonly HttpRandomAccessStreamOptions _options;
    private readonly long _length;
    private readonly EntityTagHeaderValue? _entityTag;
    private readonly DateTimeOffset? _lastModified;
    private readonly SemaphoreSlim _positionGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly ConcurrentDictionary<HttpRangeReadStream, byte> _activeRanges = new();
    private readonly object _slowdownLock = new();

    private DateTimeOffset _notBefore;
    private Exception? _fault;
    private long _position;
    private int _disposed;

    private HttpRandomAccessStream(
        HttpClient client,
        Uri requestUri,
        HttpRandomAccessStreamOptions options,
        long length,
        EntityTagHeaderValue? entityTag,
        DateTimeOffset? lastModified)
    {
        _client = client;
        _requestUri = requestUri;
        _options = options;
        _length = length;
        _entityTag = entityTag;
        _lastModified = lastModified;
    }

    /// <summary>Opens and validates a remote byte-range resource.</summary>
    public static async Task<HttpRandomAccessStream> OpenAsync(
        HttpClient client,
        Uri requestUri,
        HttpRandomAccessStreamOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(requestUri);
        if (!requestUri.IsAbsoluteUri)
        {
            throw new ArgumentException("The request URI must be absolute.", nameof(requestUri));
        }

        options ??= HttpRandomAccessStreamOptions.Default;

        using HttpResponseMessage response = await SendProbeAsync(
            client,
            requestUri,
            options,
            cancellationToken).ConfigureAwait(false);

        long length;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            ContentRangeHeaderValue range = GetContentRange(response);
            if (range.From != 0 || range.To != 0 || range.Length is null)
            {
                throw new IOException("The HTTP range probe returned an invalid Content-Range.");
            }

            length = range.Length.Value;
            ValidateIdentityEncoding(response);
            await using Stream body = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            byte[] probe = new byte[1];
            await body.ReadExactlyAsync(probe, cancellationToken).ConfigureAwait(false);
        }
        else if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable &&
                 response.Content.Headers.ContentRange is { Length: 0 })
        {
            length = 0;
        }
        else
        {
            throw CreateUnexpectedStatusException(response, "The server did not honor the HTTP byte-range probe.");
        }

        EntityTagHeaderValue? strongEntityTag = response.Headers.ETag is { IsWeak: false } tag
            ? new EntityTagHeaderValue(tag.Tag, isWeak: false)
            : null;
        DateTimeOffset? lastModified = response.Content.Headers.LastModified;

        if (options.ValidationMode == HttpRepresentationValidationMode.RequireStrongValidator &&
            strongEntityTag is null)
        {
            throw new IOException("The HTTP resource did not provide the required strong ETag.");
        }

        if (options.ValidationMode == HttpRepresentationValidationMode.None)
        {
            strongEntityTag = null;
            lastModified = null;
        }
        else if (strongEntityTag is not null)
        {
            lastModified = null;
        }

        return new HttpRandomAccessStream(
            client,
            requestUri,
            options,
            length,
            strongEntityTag,
            lastModified);
    }

    /// <summary>Gets the requested resource URI.</summary>
    public Uri RequestUri => _requestUri;

    /// <summary>Gets the options used by this stream.</summary>
    public HttpRandomAccessStreamOptions Options => _options;

    /// <inheritdoc />
    public bool CanReadAt => !IsDisposed && Volatile.Read(ref _fault) is null;

    /// <inheritdoc />
    public bool CanWriteAt => false;

    /// <inheritdoc />
    public override bool CanRead => CanReadAt;

    /// <inheritdoc />
    public override bool CanSeek => !IsDisposed && Volatile.Read(ref _fault) is null;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length
    {
        get
        {
            ThrowIfUnavailable();
            return _length;
        }
    }

    /// <inheritdoc />
    public override long Position
    {
        get
        {
            ThrowIfUnavailable();
            return Interlocked.Read(ref _position);
        }
        set
        {
            ThrowIfUnavailable();
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            Interlocked.Exchange(ref _position, value);
        }
    }

    /// <inheritdoc />
    public int ReadAt(Span<byte> buffer, long offset)
    {
        ThrowIfUnavailable();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        int requested = GetBoundedReadLength(buffer.Length, offset);
        if (requested == 0)
        {
            return 0;
        }

        using Stream range = OpenReadRange(offset, requested);
        range.ReadExactly(buffer[..requested]);
        return requested;
    }

    /// <inheritdoc />
    public async ValueTask<int> ReadAtAsync(
        Memory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        int requested = GetBoundedReadLength(buffer.Length, offset);
        if (requested == 0)
        {
            return 0;
        }

        await using Stream range = await OpenReadRangeAsync(
            offset,
            requested,
            cancellationToken).ConfigureAwait(false);
        await range.ReadExactlyAsync(buffer[..requested], cancellationToken).ConfigureAwait(false);
        return requested;
    }

    /// <inheritdoc />
    public void WriteAt(ReadOnlySpan<byte> buffer, long offset) =>
        throw new NotSupportedException("HTTP random-access streams are read-only.");

    /// <inheritdoc />
    public ValueTask WriteAtAsync(
        ReadOnlyMemory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NotSupportedException("HTTP random-access streams are read-only."));

    /// <inheritdoc />
    public async ValueTask<Stream> OpenReadRangeAsync(
        long offset,
        long length,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        long boundedLength = GetBoundedRangeLength(length, offset);
        if (boundedLength == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new BoundedRandomAccessReadStream(this, offset, 0);
        }

        HttpResponseMessage response = await SendRangeResponseAsync(
            offset,
            boundedLength,
            cancellationToken).ConfigureAwait(false);
        return await CreateRangeStreamAsync(response, offset, boundedLength, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        ThrowIfUnavailable();
        _positionGate.Wait();
        try
        {
            int read = ReadAt(buffer, _position);
            _position += read;
            return read;
        }
        finally
        {
            _positionGate.Release();
        }
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        await _positionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int read = await ReadAtAsync(buffer, _position, cancellationToken).ConfigureAwait(false);
            _position += read;
            return read;
        }
        finally
        {
            _positionGate.Release();
        }
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfUnavailable();
        _positionGate.Wait();
        try
        {
            long originOffset = origin switch
            {
                SeekOrigin.Begin => 0,
                SeekOrigin.Current => _position,
                SeekOrigin.End => _length,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            long newPosition = checked(originOffset + offset);
            ArgumentOutOfRangeException.ThrowIfNegative(newPosition);
            _position = newPosition;
            return newPosition;
        }
        finally
        {
            _positionGate.Release();
        }
    }

    /// <inheritdoc />
    public override void Flush() => ThrowIfUnavailable();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        return cancellationToken.IsCancellationRequested
            ? Task.FromCanceled(cancellationToken)
            : Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void SetLength(long value) =>
        throw new NotSupportedException("HTTP random-access streams are read-only.");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("HTTP random-access streams are read-only.");

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lifetimeCancellation.Cancel();
            foreach (HttpRangeReadStream range in _activeRanges.Keys)
            {
                range.AbortFromParent();
            }

            _activeRanges.Clear();
        }

        base.Dispose(disposing);
    }

    private Stream OpenReadRange(long offset, long length)
    {
        long boundedLength = GetBoundedRangeLength(length, offset);
        if (boundedLength == 0)
        {
            return new BoundedRandomAccessReadStream(this, offset, 0);
        }

        HttpResponseMessage response = SendRangeResponse(offset, boundedLength, CancellationToken.None);
        try
        {
            Stream body = response.Content.ReadAsStream(_lifetimeCancellation.Token);
            return RegisterRange(new HttpRangeReadStream(this, response, body, offset, boundedLength));
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async ValueTask<Stream> CreateRangeStreamAsync(
        HttpResponseMessage response,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        try
        {
            Stream body = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return RegisterRange(new HttpRangeReadStream(this, response, body, offset, length));
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private HttpRangeReadStream RegisterRange(HttpRangeReadStream range)
    {
        if (IsDisposed || !_activeRanges.TryAdd(range, 0))
        {
            range.AbortFromParent();
            ThrowIfUnavailable();
        }

        return range;
    }

    private void UnregisterRange(HttpRangeReadStream range) =>
        _activeRanges.TryRemove(range, out _);

    private HttpResponseMessage SendRangeResponse(
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        int slowdownAttempt = 0;
        int representationAttempt = 0;
        while (true)
        {
            ThrowIfUnavailable();
            WaitForSlowdown(cancellationToken);
            using var request = CreateRequest(_requestUri, offset, checked(offset + length - 1), _entityTag, _lastModified);
            using CancellationTokenSource linked = CreateLinkedTokenSource(cancellationToken);
            HttpResponseMessage response = _client.Send(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token);

            if (IsSlowdown(response.StatusCode))
            {
                if (slowdownAttempt++ >= _options.SlowdownRetryCount)
                {
                    HttpRequestException exception = CreateUnexpectedStatusException(
                        response,
                        "The server continued to throttle the HTTP byte-range request.");
                    response.Dispose();
                    throw exception;
                }

                try
                {
                    RegisterSlowdown(response, slowdownAttempt);
                }
                finally
                {
                    response.Dispose();
                }

                continue;
            }

            if (IsRepresentationChange(response, offset, length, out string? reason))
            {
                response.Dispose();
                if (representationAttempt++ < _options.RepresentationChangeRetryCount)
                {
                    DelayForRetry(representationAttempt, cancellationToken);
                    continue;
                }

                throw FaultRepresentation(reason!);
            }

            ValidateRangeResponse(response, offset, length);
            return response;
        }
    }

    private async Task<HttpResponseMessage> SendRangeResponseAsync(
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        int slowdownAttempt = 0;
        int representationAttempt = 0;
        while (true)
        {
            ThrowIfUnavailable();
            await WaitForSlowdownAsync(cancellationToken).ConfigureAwait(false);
            using var request = CreateRequest(_requestUri, offset, checked(offset + length - 1), _entityTag, _lastModified);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
            HttpResponseMessage response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token).ConfigureAwait(false);

            if (IsSlowdown(response.StatusCode))
            {
                if (slowdownAttempt++ >= _options.SlowdownRetryCount)
                {
                    HttpRequestException exception = CreateUnexpectedStatusException(
                        response,
                        "The server continued to throttle the HTTP byte-range request.");
                    response.Dispose();
                    throw exception;
                }

                try
                {
                    RegisterSlowdown(response, slowdownAttempt);
                }
                finally
                {
                    response.Dispose();
                }

                continue;
            }

            if (IsRepresentationChange(response, offset, length, out string? reason))
            {
                response.Dispose();
                if (representationAttempt++ < _options.RepresentationChangeRetryCount)
                {
                    await DelayForRetryAsync(representationAttempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw FaultRepresentation(reason!);
            }

            ValidateRangeResponse(response, offset, length);
            return response;
        }
    }

    private HttpResponseMessage ResumeRange(long offset, long length, CancellationToken cancellationToken) =>
        SendRangeResponse(offset, length, cancellationToken);

    private Task<HttpResponseMessage> ResumeRangeAsync(long offset, long length, CancellationToken cancellationToken) =>
        SendRangeResponseAsync(offset, length, cancellationToken);

    private void ValidateRangeResponse(HttpResponseMessage response, long offset, long length)
    {
        try
        {
            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                throw CreateUnexpectedStatusException(
                    response,
                    "The server did not honor the HTTP byte-range request.");
            }

            ContentRangeHeaderValue range = GetContentRange(response);
            long expectedLast = checked(offset + length - 1);
            if (range.From != offset || range.To != expectedLast || range.Length != _length)
            {
                throw FaultRepresentation("The HTTP Content-Range no longer matches the opened representation snapshot.");
            }

            if (response.Content.Headers.ContentLength is long contentLength && contentLength != length)
            {
                throw new IOException("The HTTP Content-Length does not match the requested range.");
            }

            ValidateIdentityEncoding(response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private bool IsRepresentationChange(
        HttpResponseMessage response,
        long offset,
        long length,
        out string? reason)
    {
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            reason = "The HTTP resource no longer satisfies its opened representation validator.";
            return true;
        }

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable &&
            response.Content.Headers.ContentRange?.Length is long actualLength &&
            actualLength != _length)
        {
            reason = "The HTTP resource length changed after the stream was opened.";
            return true;
        }

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
            if (range?.Length is long partialLength && partialLength != _length)
            {
                reason = "The HTTP resource length changed after the stream was opened.";
                return true;
            }

            if (_entityTag is not null && response.Headers.ETag is { } actualTag &&
                !_entityTag.Equals(actualTag))
            {
                reason = "The HTTP resource ETag changed after the stream was opened.";
                return true;
            }

            if (_lastModified is not null && response.Content.Headers.LastModified is { } actualModified &&
                actualModified != _lastModified)
            {
                reason = "The HTTP resource Last-Modified value changed after the stream was opened.";
                return true;
            }
        }

        reason = null;
        return false;
    }

    private HttpRepresentationChangedException FaultRepresentation(string message)
    {
        var exception = new HttpRepresentationChangedException(message);
        Exception winner = Interlocked.CompareExchange(ref _fault, exception, null) ?? exception;
        return winner as HttpRepresentationChangedException ?? exception;
    }

    private void RegisterSlowdown(HttpResponseMessage response, int attempt)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset requested = response.Headers.RetryAfter switch
        {
            { Date: { } date } => date,
            { Delta: { } delta } => now + delta,
            _ => now + GetBackoff(attempt),
        };
        TimeSpan wait = requested - now;
        if (wait > _options.MaximumSlowdownWait)
        {
            throw new HttpRequestException(
                "The server requested a slowdown interval longer than the configured maximum.",
                inner: null,
                response.StatusCode);
        }

        lock (_slowdownLock)
        {
            if (requested > _notBefore)
            {
                _notBefore = requested;
            }
        }
    }

    private void WaitForSlowdown(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _lifetimeCancellation.Token.ThrowIfCancellationRequested();
            TimeSpan delay;
            lock (_slowdownLock)
            {
                delay = _notBefore - DateTimeOffset.UtcNow;
            }

            if (delay <= TimeSpan.Zero)
            {
                return;
            }

            using CancellationTokenSource linked = CreateLinkedTokenSource(cancellationToken);
            Task.Delay(delay, linked.Token).GetAwaiter().GetResult();
        }
    }

    private async Task WaitForSlowdownAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan delay;
            lock (_slowdownLock)
            {
                delay = _notBefore - DateTimeOffset.UtcNow;
            }

            if (delay <= TimeSpan.Zero)
            {
                return;
            }

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
            await Task.Delay(delay, linked.Token).ConfigureAwait(false);
        }
    }

    private void DelayForRetry(int attempt, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CreateLinkedTokenSource(cancellationToken);
        Task.Delay(GetBackoff(attempt), linked.Token).GetAwaiter().GetResult();
    }

    private async Task DelayForRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        await Task.Delay(GetBackoff(attempt), linked.Token).ConfigureAwait(false);
    }

    private TimeSpan GetBackoff(int attempt)
        => GetBackoff(_options, attempt);

    private static TimeSpan GetBackoff(HttpRandomAccessStreamOptions options, int attempt)
    {
        double multiplier = Math.Pow(2, Math.Min(attempt - 1, 20));
        double jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
        double milliseconds = Math.Min(
            options.MaximumSlowdownWait.TotalMilliseconds,
            options.RetryBaseDelay.TotalMilliseconds * multiplier * jitter);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private CancellationTokenSource CreateLinkedTokenSource(CancellationToken cancellationToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);

    private int GetBoundedReadLength(int requested, long offset) =>
        offset >= _length ? 0 : (int)Math.Min(requested, _length - offset);

    private long GetBoundedRangeLength(long requested, long offset) =>
        offset >= _length ? 0 : Math.Min(requested, _length - offset);

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Exception? fault = Volatile.Read(ref _fault);
        if (fault is not null)
        {
            ExceptionDispatchInfo.Capture(fault).Throw();
        }
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private static bool IsSlowdown(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;

    private static async Task<HttpResponseMessage> SendProbeAsync(
        HttpClient client,
        Uri requestUri,
        HttpRandomAccessStreamOptions options,
        CancellationToken cancellationToken)
    {
        int slowdownAttempt = 0;
        while (true)
        {
            using var request = CreateRequest(requestUri, 0, 0, entityTag: null, lastModified: null);
            HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (!IsSlowdown(response.StatusCode))
            {
                return response;
            }

            if (slowdownAttempt++ >= options.SlowdownRetryCount)
            {
                HttpRequestException exception = CreateUnexpectedStatusException(
                    response,
                    "The server continued to throttle the HTTP byte-range probe.");
                response.Dispose();
                throw exception;
            }

            try
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                DateTimeOffset requested = response.Headers.RetryAfter switch
                {
                    { Date: { } date } => date,
                    { Delta: { } delta } => now + delta,
                    _ => now + GetBackoff(options, slowdownAttempt),
                };
                TimeSpan delay = requested - now;
                if (delay > options.MaximumSlowdownWait)
                {
                    throw new HttpRequestException(
                        "The server requested a slowdown interval longer than the configured maximum.",
                        inner: null,
                        response.StatusCode);
                }

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                response.Dispose();
            }
        }
    }

    private static HttpRequestMessage CreateRequest(
        Uri requestUri,
        long first,
        long last,
        EntityTagHeaderValue? entityTag,
        DateTimeOffset? lastModified)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Range = new RangeHeaderValue(first, last);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        if (entityTag is not null)
        {
            request.Headers.IfMatch.Add(entityTag);
        }
        else if (lastModified is not null)
        {
            request.Headers.IfUnmodifiedSince = lastModified;
        }

        return request;
    }

    private static ContentRangeHeaderValue GetContentRange(HttpResponseMessage response) =>
        response.Content.Headers.ContentRange
        ?? throw new IOException("The HTTP partial response did not include Content-Range.");

    private static void ValidateIdentityEncoding(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentEncoding.Any(
            static encoding => !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException("Encoded HTTP byte-range responses are not supported.");
        }
    }

    private static HttpRequestException CreateUnexpectedStatusException(
        HttpResponseMessage response,
        string message) =>
        new(message, inner: null, response.StatusCode);

    private sealed class HttpRangeReadStream : Stream
    {
        private readonly HttpRandomAccessStream _owner;
        private readonly long _start;
        private readonly long _length;
        private HttpResponseMessage? _response;
        private Stream? _body;
        private long _position;
        private int _resumeAttempts;
        private Exception? _fault;
        private int _disposed;

        internal HttpRangeReadStream(
            HttpRandomAccessStream owner,
            HttpResponseMessage response,
            Stream body,
            long start,
            long length)
        {
            _owner = owner;
            _response = response;
            _body = body;
            _start = start;
            _length = length;
        }

        public override bool CanRead => !IsDisposed && _fault is null;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length { get { ThrowIfUnavailable(); return _length; } }
        public override long Position
        {
            get { ThrowIfUnavailable(); return _position; }
            set => throw new NotSupportedException("HTTP range streams do not support seeking.");
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            ThrowIfUnavailable();
            int requested = (int)Math.Min(buffer.Length, _length - _position);
            while (requested > 0)
            {
                try
                {
                    int read = _body!.Read(buffer[..requested]);
                    if (read > 0)
                    {
                        _position += read;
                        return read;
                    }

                    Resume(CancellationToken.None);
                }
                catch (Exception exception) when (CanResume(exception, CancellationToken.None))
                {
                    Resume(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    Fault(exception);
                    throw;
                }
            }

            return 0;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ThrowIfUnavailable();
            int requested = (int)Math.Min(buffer.Length, _length - _position);
            while (requested > 0)
            {
                try
                {
                    int read = await _body!.ReadAsync(buffer[..requested], cancellationToken).ConfigureAwait(false);
                    if (read > 0)
                    {
                        _position += read;
                        return read;
                    }

                    await ResumeAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (CanResume(exception, cancellationToken))
                {
                    await ResumeAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
                {
                    Fault(exception);
                    throw;
                }
                catch (Exception exception)
                {
                    Fault(exception);
                    throw;
                }
            }

            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override void Flush() => ThrowIfUnavailable();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        internal void AbortFromParent()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                DisposeResponse();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                DisposeResponse();
                _owner.UnregisterRange(this);
            }

            base.Dispose(disposing);
        }

        private void Resume(CancellationToken cancellationToken)
        {
            try
            {
                EnsureResumeAvailable();
                DisposeResponse();
                long remaining = _length - _position;
                HttpResponseMessage response = _owner.ResumeRange(
                    checked(_start + _position),
                    remaining,
                    cancellationToken);
                try
                {
                    _body = response.Content.ReadAsStream(cancellationToken);
                    _response = response;
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }
            catch (Exception exception)
            {
                Fault(exception);
                throw;
            }
        }

        private async Task ResumeAsync(CancellationToken cancellationToken)
        {
            try
            {
                EnsureResumeAvailable();
                DisposeResponse();
                long remaining = _length - _position;
                HttpResponseMessage response = await _owner.ResumeRangeAsync(
                    checked(_start + _position),
                    remaining,
                    cancellationToken).ConfigureAwait(false);
                try
                {
                    _body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    _response = response;
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }
            catch (Exception exception)
            {
                Fault(exception);
                throw;
            }
        }

        private bool CanResume(Exception exception, CancellationToken cancellationToken) =>
            !cancellationToken.IsCancellationRequested &&
            exception is not HttpRepresentationChangedException &&
            _resumeAttempts < _owner._options.RangeResumeRetryCount;

        private void EnsureResumeAvailable()
        {
            if (_resumeAttempts++ >= _owner._options.RangeResumeRetryCount)
            {
                var exception = new IOException("The HTTP range body ended before the requested range was complete.");
                Fault(exception);
                throw exception;
            }
        }

        private void Fault(Exception exception)
        {
            Interlocked.CompareExchange(ref _fault, exception, null);
            DisposeResponse();
        }

        private void DisposeResponse()
        {
            Interlocked.Exchange(ref _body, null)?.Dispose();
            Interlocked.Exchange(ref _response, null)?.Dispose();
        }

        private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        private void ThrowIfUnavailable()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (_fault is { } fault)
            {
                ExceptionDispatchInfo.Capture(fault).Throw();
            }

            _owner.ThrowIfUnavailable();
        }
    }
}
