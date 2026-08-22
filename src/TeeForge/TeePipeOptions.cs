using System.Buffers;
using System.IO.Pipelines;

namespace TeeForge;

/// <summary>Provides immutable options for a <see cref="TeePipe"/>.</summary>
public class TeePipeOptions
{
    private const int DefaultMinimumSegmentSize = 4096;
    private const int DefaultPauseWriterThreshold = 65536;
    private const int DefaultResumeWriterThreshold = DefaultPauseWriterThreshold / 2;

    /// <summary>Gets the default options.</summary>
    public static TeePipeOptions Default { get; } = new();

    /// <summary>Initializes a new options instance.</summary>
    /// <param name="pool">The memory pool used for buffer management.</param>
    /// <param name="readerScheduler">The scheduler for reader callbacks and continuations.</param>
    /// <param name="writerScheduler">The scheduler for writer callbacks and continuations.</param>
    /// <param name="pauseWriterThreshold">Bytes unexamined by the slowest active reader before a flush pauses; zero disables backpressure.</param>
    /// <param name="resumeWriterThreshold">Bytes unexamined by every active reader below which a paused flush resumes.</param>
    /// <param name="minimumSegmentSize">The minimum requested segment size.</param>
    /// <param name="useSynchronizationContext">Whether async operations may capture a custom synchronization context.</param>
    /// <param name="readerFailureBehavior">How a reader exception affects other endpoints.</param>
    public TeePipeOptions(
        MemoryPool<byte>? pool = null,
        PipeScheduler? readerScheduler = null,
        PipeScheduler? writerScheduler = null,
        long pauseWriterThreshold = -1,
        long resumeWriterThreshold = -1,
        int minimumSegmentSize = -1,
        bool useSynchronizationContext = true,
        TeePipeReaderFailureBehavior readerFailureBehavior = TeePipeReaderFailureBehavior.Continue)
    {
        MinimumSegmentSize = minimumSegmentSize == -1 ? DefaultMinimumSegmentSize : minimumSegmentSize;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MinimumSegmentSize, nameof(minimumSegmentSize));

        if (pauseWriterThreshold == -1)
        {
            pauseWriterThreshold = DefaultPauseWriterThreshold;
        }
        else
        {
            ArgumentOutOfRangeException.ThrowIfNegative(pauseWriterThreshold);
        }

        if (resumeWriterThreshold == -1)
        {
            resumeWriterThreshold = DefaultResumeWriterThreshold;
        }
        else if (resumeWriterThreshold == 0)
        {
            resumeWriterThreshold = 1;
        }

        if (resumeWriterThreshold < 0 || (pauseWriterThreshold > 0 && resumeWriterThreshold > pauseWriterThreshold))
        {
            throw new ArgumentOutOfRangeException(nameof(resumeWriterThreshold));
        }

        if (!Enum.IsDefined(readerFailureBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(readerFailureBehavior));
        }

        Pool = pool ?? MemoryPool<byte>.Shared;
        ReaderScheduler = readerScheduler ?? PipeScheduler.ThreadPool;
        WriterScheduler = writerScheduler ?? PipeScheduler.ThreadPool;
        PauseWriterThreshold = pauseWriterThreshold;
        ResumeWriterThreshold = resumeWriterThreshold;
        UseSynchronizationContext = useSynchronizationContext;
        ReaderFailureBehavior = readerFailureBehavior;
    }

    /// <summary>Gets whether async operations may capture a custom synchronization context.</summary>
    public bool UseSynchronizationContext { get; }

    /// <summary>Gets the slowest-reader pause threshold.</summary>
    public long PauseWriterThreshold { get; }

    /// <summary>Gets the all-reader resume threshold.</summary>
    public long ResumeWriterThreshold { get; }

    /// <summary>Gets the minimum requested segment size.</summary>
    public int MinimumSegmentSize { get; }

    /// <summary>Gets the writer scheduler.</summary>
    public PipeScheduler WriterScheduler { get; }

    /// <summary>Gets the shared reader scheduler.</summary>
    public PipeScheduler ReaderScheduler { get; }

    /// <summary>Gets the memory pool.</summary>
    public MemoryPool<byte> Pool { get; }

    /// <summary>Gets how reader exceptions affect other endpoints.</summary>
    public TeePipeReaderFailureBehavior ReaderFailureBehavior { get; }

    internal bool IsDefaultSharedMemoryPool => ReferenceEquals(Pool, MemoryPool<byte>.Shared);

    internal static int InitialSegmentPoolSize => 4;

    internal static int MaxSegmentPoolSize => 256;
}
