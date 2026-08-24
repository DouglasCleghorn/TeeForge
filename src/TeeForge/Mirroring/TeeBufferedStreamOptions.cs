namespace TeeForge.Mirroring;

/// <summary>Provides immutable options for a <see cref="TeeBufferedStream"/>.</summary>
public class TeeBufferedStreamOptions : TeeStreamOptions
{
    private const int DefaultBufferSize = 4096;

    /// <summary>Gets the default buffered-stream options.</summary>
    public static new TeeBufferedStreamOptions Default { get; } = new();

    /// <summary>Initializes a new buffered-stream options instance.</summary>
    /// <param name="mismatchBehavior">How successful differences are handled.</param>
    /// <param name="synchronousMode">How independent synchronous calls are dispatched.</param>
    /// <param name="leaveOpen">Whether disposing the wrapper leaves every destination open.</param>
    /// <param name="bufferSize">The shared buffer size in bytes.</param>
    public TeeBufferedStreamOptions(
        TeeStreamMismatchBehavior mismatchBehavior = TeeStreamMismatchBehavior.ThrowAndContinue,
        TeeStreamSynchronousMode synchronousMode = TeeStreamSynchronousMode.Sequential,
        bool leaveOpen = false,
        int bufferSize = DefaultBufferSize)
        : base(mismatchBehavior, synchronousMode, leaveOpen)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        BufferSize = bufferSize;
    }

    /// <summary>Gets the shared buffer size in bytes.</summary>
    public int BufferSize { get; }
}
