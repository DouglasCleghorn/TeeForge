namespace TeeForge.RandomAccess;

/// <summary>Provides immutable options for an <see cref="HttpRandomAccessStream"/>.</summary>
public class HttpRandomAccessStreamOptions
{
    private static readonly TimeSpan DefaultMaximumSlowdownWait = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultRetryBaseDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Gets the default options.</summary>
    public static HttpRandomAccessStreamOptions Default { get; } = new();

    /// <summary>Initializes a new HTTP random-access options instance.</summary>
    public HttpRandomAccessStreamOptions(
        HttpRepresentationValidationMode validationMode = HttpRepresentationValidationMode.WhenAvailable,
        int slowdownRetryCount = 3,
        TimeSpan? maximumSlowdownWait = null,
        int representationChangeRetryCount = 0,
        int rangeResumeRetryCount = 3,
        TimeSpan? retryBaseDelay = null)
    {
        if (!Enum.IsDefined(validationMode))
        {
            throw new ArgumentOutOfRangeException(nameof(validationMode));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(slowdownRetryCount);
        ArgumentOutOfRangeException.ThrowIfNegative(representationChangeRetryCount);
        ArgumentOutOfRangeException.ThrowIfNegative(rangeResumeRetryCount);

        TimeSpan effectiveMaximumSlowdownWait = maximumSlowdownWait ?? DefaultMaximumSlowdownWait;
        TimeSpan effectiveRetryBaseDelay = retryBaseDelay ?? DefaultRetryBaseDelay;
        if (effectiveMaximumSlowdownWait <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSlowdownWait));
        }

        if (effectiveRetryBaseDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryBaseDelay));
        }

        ValidationMode = validationMode;
        SlowdownRetryCount = slowdownRetryCount;
        MaximumSlowdownWait = effectiveMaximumSlowdownWait;
        RepresentationChangeRetryCount = representationChangeRetryCount;
        RangeResumeRetryCount = rangeResumeRetryCount;
        RetryBaseDelay = effectiveRetryBaseDelay;
    }

    /// <summary>Gets how the remote representation is validated.</summary>
    public HttpRepresentationValidationMode ValidationMode { get; }

    /// <summary>Gets the number of retries allowed after HTTP 429 or 503 responses.</summary>
    public int SlowdownRetryCount { get; }

    /// <summary>Gets the maximum server-requested slowdown delay the stream will wait.</summary>
    public TimeSpan MaximumSlowdownWait { get; }

    /// <summary>Gets the retries allowed while continuing to target the originally opened representation.</summary>
    public int RepresentationChangeRetryCount { get; }

    /// <summary>Gets the number of times an interrupted range body may be resumed.</summary>
    public int RangeResumeRetryCount { get; }

    /// <summary>Gets the initial retry delay used for exponential backoff.</summary>
    public TimeSpan RetryBaseDelay { get; }
}
