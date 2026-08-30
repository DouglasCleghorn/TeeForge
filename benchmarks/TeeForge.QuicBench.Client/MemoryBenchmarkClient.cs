using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using TeeForge.Networking;

namespace TeeForge.QuicBench.Client;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal static class MemoryBenchmarkClient
{
    private static readonly SslApplicationProtocol Protocol = new("teeforge-quic-memory-benchmark-v1");
    private static long _checksum;

    internal static async Task<int> RunAsync(BenchmarkArguments arguments)
    {
        int port = arguments.GetInt32("port", 45678);
        long length = arguments.Contains("memory-size-mib")
            ? arguments.GetInt32("memory-size-mib", 64) * 1024L * 1024L
            : arguments.GetInt32("memory-size-gib", 3) * 1024L * 1024L * 1024L;
        int segmentSize = checked(arguments.GetInt32("memory-segment-mib", 64) * 1024 * 1024);
        long randomBytes = arguments.GetInt32("random-mib", 64) * 1024L * 1024L;
        int sequentialIterations = arguments.GetInt32("sequential-iterations", 1);
        int randomIterations = arguments.GetInt32("random-iterations", 2);
        int compressionThreshold = arguments.GetInt32("compression-threshold", 16 * 1024);
        int[] sequentialBlockSizes = arguments.GetInt32List(
            "sequential-block-sizes",
            "65536,1048576");
        int[] randomBlockSizes = arguments.GetInt32List(
            "random-block-sizes",
            "4096,65536,1048576");
        int[] queueDepths = arguments.GetInt32List("queue-depths", "1,4,16,32");
        QuicStreamCompression compression = ParseCompression(arguments.Get("compression", "none"));
        string outputPath = Path.GetFullPath(
            arguments.Get(
                "output",
                Path.Combine("artifacts", "quic-memory-benchmark", "results.json")));

        Console.WriteLine(
            $"Allocating {length / 1024d / 1024d / 1024d:F1} GiB across " +
            $"{segmentSize / 1024 / 1024} MiB MemoryStream segments...");
        using var localStore = new SegmentedMemoryStore(length, segmentSize);

        var connectionOptions = new MutualQuicConnectionOptions(
            arguments.Required("certificate"),
            arguments.Required("private-key"),
            arguments.Required("trusted-peer-certificate"),
            Protocol,
            maximumInboundBidirectionalStreams: 512,
            maximumPendingNamedStreams: 64,
            maximumRandomAccessRequestSize: 1024 * 1024,
            maximumRandomAccessSessions: 16);
        await using MutualQuicConnection connection = await MutualQuicConnection.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, port),
            "localhost",
            connectionOptions);
        await using (NamedQuicStream ready = await connection.AcceptStreamAsync())
        {
            if (!StringComparer.Ordinal.Equals(ready.Name, "memory-benchmark-ready") || ready.ReadByte() != 1)
            {
                throw new InvalidDataException("The memory benchmark readiness handshake was invalid.");
            }
        }

        QuicRandomAccessChannel remoteRead = await connection.OpenRandomAccessAsync(
            "memory-random-read",
            new QuicRandomAccessOptions(compression, compressionThreshold));
        QuicRandomAccessChannel remoteWrite = await connection.OpenRandomAccessAsync(
            "memory-random-write",
            new QuicRandomAccessOptions(compression, compressionThreshold));
        QuicRandomAccessChannel transferRead = await connection.OpenRandomAccessAsync(
            "transfer-random-read",
            new QuicRandomAccessOptions(compression, compressionThreshold));
        QuicRandomAccessChannel transferWrite = await connection.OpenRandomAccessAsync(
            "transfer-random-write",
            new QuicRandomAccessOptions(compression, compressionThreshold));

        var results = new List<BenchmarkResult>();
        int sequence = 0;
        long warmupLength = Math.Min(length, 16L * 1024 * 1024);
        Console.WriteLine("Warming in-memory sequential and random paths...");
        await RunSequentialReadDirectAsync(localStore, warmupLength, sequentialBlockSizes[0]);
        await RunSequentialReadQuicAsync(
            connection,
            warmupLength,
            sequentialBlockSizes[0],
            compression,
            sequence++);
        await RunSequentialReadQuicDirectAsync(
            connection,
            warmupLength,
            sequentialBlockSizes[0],
            compression,
            sequence++);
        await RunRandomReadDirectAsync(
            localStore,
            length,
            randomBlockSizes[0],
            queueDepths[0],
            Math.Min(randomBytes, 1024 * 1024));
        await RunRandomReadQuicAsync(
            remoteRead,
            length,
            randomBlockSizes[0],
            queueDepths[0],
            Math.Min(randomBytes, 1024 * 1024));
        await RunRandomReadQuicAsync(
            transferRead,
            length,
            randomBlockSizes[0],
            queueDepths[0],
            Math.Min(randomBytes, 1024 * 1024));

        foreach (int blockSize in sequentialBlockSizes)
        {
            for (int iteration = 1; iteration <= sequentialIterations; iteration++)
            {
                results.Add(await MeasureAsync(
                    "MemoryRead",
                    "Direct",
                    blockSize,
                    queueDepth: 1,
                    iteration,
                    length,
                    DivideRoundUp(length, blockSize),
                    () => RunSequentialReadDirectAsync(localStore, length, blockSize)));
                results.Add(await MeasureAsync(
                    "MemoryRead",
                    $"QUIC-Memory({compression})",
                    blockSize,
                    queueDepth: 1,
                    iteration,
                    length,
                    DivideRoundUp(length, blockSize),
                    () => RunSequentialReadQuicAsync(
                        connection,
                        length,
                        blockSize,
                        compression,
                        sequence++)));
                results.Add(await MeasureAsync(
                    "MemoryRead",
                    $"QUIC-Direct({compression})",
                    blockSize,
                    queueDepth: 1,
                    iteration,
                    length,
                    DivideRoundUp(length, blockSize),
                    () => RunSequentialReadQuicDirectAsync(
                        connection,
                        length,
                        blockSize,
                        compression,
                        sequence++)));
                results.Add(await MeasureAsync(
                    "MemoryWrite",
                    "Direct",
                    blockSize,
                    queueDepth: 1,
                    iteration,
                    length,
                    DivideRoundUp(length, blockSize),
                    () => RunSequentialWriteDirectAsync(localStore, length, blockSize)));
                results.Add(await MeasureAsync(
                    "MemoryWrite",
                    $"QUIC-Memory({compression})",
                    blockSize,
                    queueDepth: 1,
                    iteration,
                    length,
                    DivideRoundUp(length, blockSize),
                    () => RunSequentialWriteQuicAsync(
                        connection,
                        length,
                        blockSize,
                        compression,
                        sequence++)));
                results.Add(await MeasureAsync(
                    "MemoryWrite",
                    $"QUIC-Direct({compression})",
                    blockSize,
                    queueDepth: 1,
                    iteration,
                    length,
                    DivideRoundUp(length, blockSize),
                    () => RunSequentialWriteQuicDirectAsync(
                        connection,
                        length,
                        blockSize,
                        compression,
                        sequence++)));
            }
        }

        foreach (int blockSize in randomBlockSizes)
        {
            foreach (int queueDepth in queueDepths)
            {
                int operations = GetRandomOperationCount(randomBytes, blockSize, queueDepth);
                long transferred = (long)operations * blockSize;
                for (int iteration = 1; iteration <= randomIterations; iteration++)
                {
                    results.Add(await MeasureAsync(
                        "MemoryRandomRead",
                        "Direct",
                        blockSize,
                        queueDepth,
                        iteration,
                        transferred,
                        operations,
                        () => RunRandomReadDirectAsync(
                            localStore,
                            length,
                            blockSize,
                            queueDepth,
                            transferred)));
                    results.Add(await MeasureAsync(
                        "MemoryRandomRead",
                        $"QUIC-Memory({compression})",
                        blockSize,
                        queueDepth,
                        iteration,
                        transferred,
                        operations,
                        () => RunRandomReadQuicAsync(
                            remoteRead,
                            length,
                            blockSize,
                            queueDepth,
                            transferred)));
                    results.Add(await MeasureAsync(
                        "MemoryRandomRead",
                        $"QUIC-Direct({compression})",
                        blockSize,
                        queueDepth,
                        iteration,
                        transferred,
                        operations,
                        () => RunRandomReadQuicAsync(
                            transferRead,
                            length,
                            blockSize,
                            queueDepth,
                            transferred)));
                    results.Add(await MeasureAsync(
                        "MemoryRandomWrite",
                        "Direct",
                        blockSize,
                        queueDepth,
                        iteration,
                        transferred,
                        operations,
                        () => RunRandomWriteDirectAsync(
                            localStore,
                            length,
                            blockSize,
                            queueDepth,
                            transferred)));
                    results.Add(await MeasureAsync(
                        "MemoryRandomWrite",
                        $"QUIC-Memory({compression})",
                        blockSize,
                        queueDepth,
                        iteration,
                        transferred,
                        operations,
                        () => RunRandomWriteQuicAsync(
                            remoteWrite,
                            length,
                            blockSize,
                            queueDepth,
                            transferred)));
                    results.Add(await MeasureAsync(
                        "MemoryRandomWrite",
                        $"QUIC-Direct({compression})",
                        blockSize,
                        queueDepth,
                        iteration,
                        transferred,
                        operations,
                        () => RunRandomWriteQuicAsync(
                            transferWrite,
                            length,
                            blockSize,
                            queueDepth,
                            transferred)));
                }
            }
        }

        var run = new MemoryBenchmarkRun(
            DateTimeOffset.Now,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            Environment.ProcessorCount,
            length,
            segmentSize,
            randomBytes,
            sequentialIterations,
            randomIterations,
            compression.ToString(),
            compressionThreshold,
            sequentialBlockSizes,
            randomBlockSizes,
            queueDepths,
            results);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(run, BenchmarkRun.JsonOptions));
        PrintSummary(results);
        Console.WriteLine($"RAW_RESULTS {outputPath}");
        Console.WriteLine($"CHECKSUM {Volatile.Read(ref _checksum)}");

        await using NamedQuicStream shutdown = await connection.OpenStreamAsync(
            $"memory-shutdown-{Guid.NewGuid():N}");
        shutdown.CompleteWrites();
        byte[] acknowledgement = new byte[1];
        await ReadExactlyAsync(shutdown, acknowledgement);
        return 0;
    }

    private static QuicStreamCompression ParseCompression(string value) =>
        value.ToLowerInvariant() switch
        {
            "none" => QuicStreamCompression.None,
            "fastest" or "brotlifastest" => QuicStreamCompression.BrotliFastest,
            "optimal" or "brotlioptimal" => QuicStreamCompression.BrotliOptimal,
            _ => throw new ArgumentException($"Unknown compression selection '{value}'."),
        };

    private static async Task RunSequentialReadDirectAsync(
        SegmentedMemoryStore store,
        long length,
        int blockSize)
    {
        using Stream source = store.OpenStream();
        await ReadAndDiscardAsync(source, length, blockSize);
    }

    private static async Task RunSequentialReadQuicAsync(
        MutualQuicConnection connection,
        long length,
        int blockSize,
        QuicStreamCompression compression,
        int sequence)
    {
        await using NamedQuicStream stream = await connection.OpenStreamAsync(
            $"memory-read-{sequence}",
            new NamedQuicStreamOptions(compression));
        await SendRequestAsync(stream, length, blockSize);
        stream.CompleteWrites();
        await ReadAndDiscardAsync(stream, length, blockSize);
    }

    private static async Task RunSequentialWriteDirectAsync(
        SegmentedMemoryStore store,
        long length,
        int blockSize)
    {
        using Stream destination = store.OpenStream();
        await WritePatternAsync(destination, length, blockSize);
    }

    private static async Task RunSequentialReadQuicDirectAsync(
        MutualQuicConnection connection,
        long length,
        int blockSize,
        QuicStreamCompression compression,
        int sequence)
    {
        await using NamedQuicStream stream = await connection.OpenStreamAsync(
            $"transfer-read-{sequence}",
            new NamedQuicStreamOptions(compression));
        await SendRequestAsync(stream, length, blockSize);
        stream.CompleteWrites();
        await ReadAndDiscardAsync(stream, length, blockSize);
    }

    private static async Task RunSequentialWriteQuicAsync(
        MutualQuicConnection connection,
        long length,
        int blockSize,
        QuicStreamCompression compression,
        int sequence)
    {
        await using NamedQuicStream stream = await connection.OpenStreamAsync(
            $"memory-write-{sequence}",
            new NamedQuicStreamOptions(compression));
        await SendRequestAsync(stream, length, blockSize);
        await WritePatternAsync(stream, length, blockSize);
        stream.CompleteWrites();
        byte[] acknowledgement = new byte[1];
        await ReadExactlyAsync(stream, acknowledgement);
    }

    private static async Task RunSequentialWriteQuicDirectAsync(
        MutualQuicConnection connection,
        long length,
        int blockSize,
        QuicStreamCompression compression,
        int sequence)
    {
        await using NamedQuicStream stream = await connection.OpenStreamAsync(
            $"transfer-write-{sequence}",
            new NamedQuicStreamOptions(compression));
        await SendRequestAsync(stream, length, blockSize);
        await WritePatternAsync(stream, length, blockSize);
        stream.CompleteWrites();
        byte[] acknowledgement = new byte[1];
        await ReadExactlyAsync(stream, acknowledgement);
    }

    private static async Task ReadAndDiscardAsync(Stream source, long length, int blockSize)
    {
        byte[] buffer = new byte[blockSize];
        long remaining = length;
        long checksum = 0;
        while (remaining > 0)
        {
            int count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
            if (count == 0)
            {
                throw new EndOfStreamException();
            }

            checksum += buffer[0];
            remaining -= count;
        }

        Interlocked.Add(ref _checksum, checksum);
    }

    private static async Task WritePatternAsync(Stream destination, long length, int blockSize)
    {
        byte[] buffer = new byte[blockSize];
        new Random(0x4D454D57).NextBytes(buffer);
        long remaining = length;
        while (remaining > 0)
        {
            int count = (int)Math.Min(buffer.Length, remaining);
            await destination.WriteAsync(buffer.AsMemory(0, count));
            remaining -= count;
        }

        await destination.FlushAsync();
    }

    private static async Task RunRandomReadDirectAsync(
        SegmentedMemoryStore store,
        long length,
        int blockSize,
        int queueDepth,
        long targetBytes) =>
        await RunRandomReadsAsync(
            (buffer, offset) => store.ReadAtAsync(buffer, offset),
            length,
            blockSize,
            queueDepth,
            targetBytes);

    private static async Task RunRandomReadQuicAsync(
        QuicRandomAccessChannel channel,
        long length,
        int blockSize,
        int queueDepth,
        long targetBytes) =>
        await RunRandomReadsAsync(
            (buffer, offset) => channel.ReadAtAsync(buffer, offset),
            length,
            blockSize,
            queueDepth,
            targetBytes);

    private static async Task RunRandomReadsAsync(
        Func<Memory<byte>, long, ValueTask<int>> read,
        long length,
        int blockSize,
        int queueDepth,
        long targetBytes)
    {
        int operationCount = GetRandomOperationCount(targetBytes, blockSize, queueDepth);
        byte[][] buffers = CreateBuffers(queueDepth, blockSize, fill: false);
        long checksum = 0;
        for (int operation = 0; operation < operationCount; operation += queueDepth)
        {
            var tasks = new Task<int>[queueDepth];
            for (int slot = 0; slot < queueDepth; slot++)
            {
                int index = operation + slot;
                tasks[slot] = read(buffers[slot], GetOffset(index, blockSize, length)).AsTask();
            }

            int[] counts = await Task.WhenAll(tasks);
            for (int slot = 0; slot < counts.Length; slot++)
            {
                if (counts[slot] != blockSize)
                {
                    throw new EndOfStreamException();
                }

                checksum += buffers[slot][0];
            }
        }

        Interlocked.Add(ref _checksum, checksum);
    }

    private static async Task RunRandomWriteDirectAsync(
        SegmentedMemoryStore store,
        long length,
        int blockSize,
        int queueDepth,
        long targetBytes) =>
        await RunRandomWritesAsync(
            (buffer, offset) => store.WriteAtAsync(buffer, offset),
            length,
            blockSize,
            queueDepth,
            targetBytes);

    private static async Task RunRandomWriteQuicAsync(
        QuicRandomAccessChannel channel,
        long length,
        int blockSize,
        int queueDepth,
        long targetBytes) =>
        await RunRandomWritesAsync(
            (buffer, offset) => channel.WriteAtAsync(buffer, offset),
            length,
            blockSize,
            queueDepth,
            targetBytes);

    private static async Task RunRandomWritesAsync(
        Func<ReadOnlyMemory<byte>, long, ValueTask> write,
        long length,
        int blockSize,
        int queueDepth,
        long targetBytes)
    {
        int operationCount = GetRandomOperationCount(targetBytes, blockSize, queueDepth);
        byte[][] buffers = CreateBuffers(queueDepth, blockSize, fill: true);
        for (int operation = 0; operation < operationCount; operation += queueDepth)
        {
            var tasks = new Task[queueDepth];
            for (int slot = 0; slot < queueDepth; slot++)
            {
                int index = operation + slot;
                tasks[slot] = write(buffers[slot], GetOffset(index, blockSize, length)).AsTask();
            }

            await Task.WhenAll(tasks);
        }
    }

    private static async Task<BenchmarkResult> MeasureAsync(
        string operation,
        string path,
        int blockSize,
        int queueDepth,
        int iteration,
        long bytes,
        int operations,
        Func<Task> action)
    {
        var stopwatch = Stopwatch.StartNew();
        await action();
        stopwatch.Stop();
        double seconds = stopwatch.Elapsed.TotalSeconds;
        var result = new BenchmarkResult(
            operation,
            path,
            blockSize,
            queueDepth,
            iteration,
            bytes,
            stopwatch.Elapsed.TotalMilliseconds,
            bytes / 1024d / 1024d / seconds,
            operations / seconds);
        Console.WriteLine(
            $"{operation,-20} {path,-20} block={blockSize,7} qd={queueDepth,2} " +
            $"iter={iteration} {result.MebibytesPerSecond,10:F1} MiB/s " +
            $"{result.OperationsPerSecond,10:F0} IOPS");
        return result;
    }

    private static void PrintSummary(IEnumerable<BenchmarkResult> results)
    {
        Console.WriteLine();
        Console.WriteLine("MEDIAN SUMMARY");
        foreach (IGrouping<(string Operation, string Path, int BlockSize, int QueueDepth), BenchmarkResult> group
            in results.GroupBy(result =>
                (result.Operation, result.Path, result.BlockSize, result.QueueDepth)))
        {
            Console.WriteLine(
                $"{group.Key.Operation,-20} {group.Key.Path,-20} " +
                $"block={group.Key.BlockSize,7} qd={group.Key.QueueDepth,2} " +
                $"{Median(group.Select(result => result.MebibytesPerSecond)),10:F1} MiB/s " +
                $"{Median(group.Select(result => result.OperationsPerSecond)),10:F0} IOPS");
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] ordered = values.Order().ToArray();
        int middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }

    private static async Task SendRequestAsync(Stream stream, long length, int blockSize)
    {
        byte[] request = new byte[12];
        BinaryPrimitives.WriteInt64LittleEndian(request, length);
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(8), blockSize);
        await stream.WriteAsync(request);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int count = await stream.ReadAsync(buffer[offset..]);
            if (count == 0)
            {
                throw new EndOfStreamException();
            }

            offset += count;
        }
    }

    private static int GetRandomOperationCount(long targetBytes, int blockSize, int queueDepth)
    {
        long operations = Math.Max(queueDepth, DivideRoundUp(targetBytes, blockSize));
        operations = DivideRoundUp(operations, queueDepth) * queueDepth;
        return checked((int)operations);
    }

    private static int DivideRoundUp(long value, int divisor) =>
        checked((int)((value + divisor - 1) / divisor));

    private static long GetOffset(int operation, int blockSize, long length)
    {
        ulong blockCount = checked((ulong)(length / blockSize));
        ulong mixed = unchecked((ulong)operation * 11400714819323198485UL + 0x4D454D42UL);
        return checked((long)(mixed % blockCount) * blockSize);
    }

    private static byte[][] CreateBuffers(int count, int size, bool fill)
    {
        byte[][] buffers = Enumerable.Range(0, count).Select(_ => new byte[size]).ToArray();
        if (fill)
        {
            var random = new Random(0x4D454D42);
            foreach (byte[] buffer in buffers)
            {
                random.NextBytes(buffer);
            }
        }

        return buffers;
    }
}
