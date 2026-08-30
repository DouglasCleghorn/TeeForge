using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using TeeForge.Networking;

namespace TeeForge.QuicBench.Client;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal static class Program
{
    private static readonly SslApplicationProtocol Protocol = new("teeforge-quic-benchmark-v1");
    private static long _checksum;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && StringComparer.OrdinalIgnoreCase.Equals(args[0], "certificates"))
            {
                var certificateArguments = new BenchmarkArguments(args[1..]);
                string output = Path.GetFullPath(certificateArguments.Required("output"));
                BenchmarkCertificates.CreatePair(output);
                Console.WriteLine($"Created benchmark identities in '{output}'.");
                return 0;
            }

            var arguments = new BenchmarkArguments(args);
            if (StringComparer.OrdinalIgnoreCase.Equals(arguments.Get("storage", "file"), "memory"))
            {
                return await MemoryBenchmarkClient.RunAsync(arguments);
            }

            int port = arguments.GetInt32("port", 45678);
            int fileSizeMiB = arguments.GetInt32("file-size-mib", 64);
            int randomMiB = arguments.GetInt32("random-mib", 8);
            int sequentialIterations = arguments.GetInt32("sequential-iterations", 3);
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
            long fileSize = fileSizeMiB * 1024L * 1024L;
            long randomBytes = randomMiB * 1024L * 1024L;
            string workDirectory = Path.GetFullPath(arguments.Required("work-dir"));
            string sourcePath = Path.Combine(workDirectory, BenchmarkFiles.SourceName);
            string outputPath = Path.GetFullPath(
                arguments.Get("output", Path.Combine(workDirectory, "quic-file-benchmark.json")));
            await BenchmarkFiles.EnsureSourceAsync(sourcePath, fileSize, CancellationToken.None);

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
                if (!StringComparer.Ordinal.Equals(ready.Name, "benchmark-ready") || ready.ReadByte() != 1)
                {
                    throw new InvalidDataException("The benchmark server readiness handshake was invalid.");
                }
            }

            QuicRandomAccessChannel remoteRead = await connection.OpenRandomAccessAsync(
                "random-read",
                new QuicRandomAccessOptions(compression, compressionThreshold));
            QuicRandomAccessChannel remoteWrite = await connection.OpenRandomAccessAsync(
                "random-write",
                new QuicRandomAccessOptions(compression, compressionThreshold));

            string directSequentialWritePath = Path.Combine(
                workDirectory,
                BenchmarkFiles.DirectSequentialWriteName);
            string directRandomWritePath = Path.Combine(workDirectory, BenchmarkFiles.DirectRandomWriteName);
            await using FileStream directRandomRead = BenchmarkFiles.OpenRandomAccessFile(
                sourcePath,
                FileAccess.Read,
                fileSize);
            await using FileStream directRandomWrite = BenchmarkFiles.OpenRandomAccessFile(
                directRandomWritePath,
                FileAccess.ReadWrite,
                fileSize);

            var results = new List<BenchmarkResult>();
            int sequence = 0;
            Console.WriteLine("Warming sequential and random paths...");
            await RunSequentialReadDirectAsync(sourcePath, fileSize, sequentialBlockSizes[0]);
            await RunSequentialReadQuicAsync(
                connection,
                fileSize,
                sequentialBlockSizes[0],
                compression,
                sequence++);
            await RunRandomReadDirectAsync(
                directRandomRead.SafeFileHandle,
                fileSize,
                randomBlockSizes[0],
                queueDepths[0],
                Math.Min(randomBytes, 1024 * 1024));
            await RunRandomReadQuicAsync(
                remoteRead,
                fileSize,
                randomBlockSizes[0],
                queueDepths[0],
                Math.Min(randomBytes, 1024 * 1024));

            foreach (int blockSize in sequentialBlockSizes)
            {
                for (int iteration = 1; iteration <= sequentialIterations; iteration++)
                {
                    results.Add(await MeasureAsync(
                        "SequentialRead",
                        "Direct",
                        blockSize,
                        queueDepth: 1,
                        iteration,
                        fileSize,
                        operations: DivideRoundUp(fileSize, blockSize),
                        () => RunSequentialReadDirectAsync(sourcePath, fileSize, blockSize)));
                    results.Add(await MeasureAsync(
                        "SequentialRead",
                        $"QUIC({compression})",
                        blockSize,
                        queueDepth: 1,
                        iteration,
                        fileSize,
                        operations: DivideRoundUp(fileSize, blockSize),
                        () => RunSequentialReadQuicAsync(
                            connection,
                            fileSize,
                            blockSize,
                            compression,
                            sequence++)));
                    results.Add(await MeasureAsync(
                        "SequentialWrite",
                        "Direct",
                        blockSize,
                        queueDepth: 1,
                        iteration,
                        fileSize,
                        operations: DivideRoundUp(fileSize, blockSize),
                        () => RunSequentialWriteDirectAsync(
                            sourcePath,
                            directSequentialWritePath,
                            fileSize,
                            blockSize)));
                    results.Add(await MeasureAsync(
                        "SequentialWrite",
                        $"QUIC({compression})",
                        blockSize,
                        queueDepth: 1,
                        iteration,
                        fileSize,
                        operations: DivideRoundUp(fileSize, blockSize),
                        () => RunSequentialWriteQuicAsync(
                            connection,
                            sourcePath,
                            fileSize,
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
                            "RandomRead",
                            "Direct",
                            blockSize,
                            queueDepth,
                            iteration,
                            transferred,
                            operations,
                            () => RunRandomReadDirectAsync(
                                directRandomRead.SafeFileHandle,
                                fileSize,
                                blockSize,
                                queueDepth,
                                transferred)));
                        results.Add(await MeasureAsync(
                            "RandomRead",
                            $"QUIC({compression})",
                            blockSize,
                            queueDepth,
                            iteration,
                            transferred,
                            operations,
                            () => RunRandomReadQuicAsync(
                                remoteRead,
                                fileSize,
                                blockSize,
                                queueDepth,
                                transferred)));
                        results.Add(await MeasureAsync(
                            "RandomWrite",
                            "Direct",
                            blockSize,
                            queueDepth,
                            iteration,
                            transferred,
                            operations,
                            () => RunRandomWriteDirectAsync(
                                directRandomWrite.SafeFileHandle,
                                fileSize,
                                blockSize,
                                queueDepth,
                                transferred)));
                        results.Add(await MeasureAsync(
                            "RandomWrite",
                            $"QUIC({compression})",
                            blockSize,
                            queueDepth,
                            iteration,
                            transferred,
                            operations,
                            () => RunRandomWriteQuicAsync(
                                remoteWrite,
                                fileSize,
                                blockSize,
                                queueDepth,
                                transferred)));
                    }
                }
            }

            var run = new BenchmarkRun(
                DateTimeOffset.Now,
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription,
                Environment.ProcessorCount,
                fileSize,
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
                $"shutdown-{Guid.NewGuid():N}");
            shutdown.CompleteWrites();
            byte[] acknowledgement = new byte[1];
            await ReadExactlyAsync(shutdown, acknowledgement);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static QuicStreamCompression ParseCompression(string value) =>
        value.ToLowerInvariant() switch
        {
            "none" => QuicStreamCompression.None,
            "fastest" or "brotlifastest" => QuicStreamCompression.BrotliFastest,
            "optimal" or "brotlioptimal" => QuicStreamCompression.BrotliOptimal,
            _ => throw new ArgumentException($"Unknown compression selection '{value}'."),
        };

    private static async Task RunSequentialReadDirectAsync(string path, long length, int blockSize)
    {
        await using var source = OpenSequentialFile(path, FileMode.Open, FileAccess.Read, blockSize);
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

    private static async Task RunSequentialReadQuicAsync(
        MutualQuicConnection connection,
        long length,
        int blockSize,
        QuicStreamCompression compression,
        int sequence)
    {
        await using NamedQuicStream stream = await connection.OpenStreamAsync(
            $"sequential-read-{sequence}",
            new NamedQuicStreamOptions(compression));
        await SendSequentialRequestAsync(stream, length, blockSize);
        stream.CompleteWrites();
        byte[] buffer = new byte[blockSize];
        long remaining = length;
        long checksum = 0;
        while (remaining > 0)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
            if (count == 0)
            {
                throw new EndOfStreamException();
            }

            checksum += buffer[0];
            remaining -= count;
        }

        Interlocked.Add(ref _checksum, checksum);
    }

    private static async Task RunSequentialWriteDirectAsync(
        string sourcePath,
        string destinationPath,
        long length,
        int blockSize)
    {
        await using var source = OpenSequentialFile(sourcePath, FileMode.Open, FileAccess.Read, blockSize);
        await using var destination = OpenSequentialFile(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            blockSize);
        await CopyExactlyAsync(source, destination, length, blockSize);
        await destination.FlushAsync();
    }

    private static async Task RunSequentialWriteQuicAsync(
        MutualQuicConnection connection,
        string sourcePath,
        long length,
        int blockSize,
        QuicStreamCompression compression,
        int sequence)
    {
        await using NamedQuicStream stream = await connection.OpenStreamAsync(
            $"sequential-write-{sequence}",
            new NamedQuicStreamOptions(compression));
        await SendSequentialRequestAsync(stream, length, blockSize);
        await using var source = OpenSequentialFile(sourcePath, FileMode.Open, FileAccess.Read, blockSize);
        await CopyExactlyAsync(source, stream, length, blockSize);
        stream.CompleteWrites();
        byte[] acknowledgement = new byte[1];
        await ReadExactlyAsync(stream, acknowledgement);
    }

    private static async Task RunRandomReadDirectAsync(
        SafeFileHandle handle,
        long fileSize,
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
                long offset = GetOffset(index, blockSize, fileSize);
                tasks[slot] = System.IO.RandomAccess.ReadAsync(handle, buffers[slot], offset).AsTask();
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

    private static async Task RunRandomReadQuicAsync(
        QuicRandomAccessChannel channel,
        long fileSize,
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
                long offset = GetOffset(index, blockSize, fileSize);
                tasks[slot] = channel.ReadAtAsync(buffers[slot], offset).AsTask();
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
        SafeFileHandle handle,
        long fileSize,
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
                long offset = GetOffset(index, blockSize, fileSize);
                tasks[slot] = System.IO.RandomAccess.WriteAsync(handle, buffers[slot], offset).AsTask();
            }

            await Task.WhenAll(tasks);
        }
    }

    private static async Task RunRandomWriteQuicAsync(
        QuicRandomAccessChannel channel,
        long fileSize,
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
                long offset = GetOffset(index, blockSize, fileSize);
                tasks[slot] = channel.WriteAtAsync(buffers[slot], offset).AsTask();
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
            $"{operation,-16} {path,-20} block={blockSize,7} qd={queueDepth,2} " +
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
            BenchmarkResult[] ordered = group.OrderBy(result => result.MebibytesPerSecond).ToArray();
            double throughput = Median(ordered.Select(result => result.MebibytesPerSecond));
            double iops = Median(ordered.Select(result => result.OperationsPerSecond));
            Console.WriteLine(
                $"{group.Key.Operation,-16} {group.Key.Path,-20} " +
                $"block={group.Key.BlockSize,7} qd={group.Key.QueueDepth,2} " +
                $"{throughput,10:F1} MiB/s {iops,10:F0} IOPS");
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

    private static FileStream OpenSequentialFile(
        string path,
        FileMode mode,
        FileAccess access,
        int blockSize) =>
        new(
            path,
            new FileStreamOptions
            {
                Access = access,
                BufferSize = blockSize,
                Mode = mode,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                Share = FileShare.ReadWrite,
            });

    private static async Task SendSequentialRequestAsync(
        Stream stream,
        long length,
        int blockSize)
    {
        byte[] request = new byte[12];
        BinaryPrimitives.WriteInt64LittleEndian(request, length);
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(8), blockSize);
        await stream.WriteAsync(request);
    }

    private static async Task CopyExactlyAsync(
        Stream source,
        Stream destination,
        long length,
        int blockSize)
    {
        byte[] buffer = new byte[blockSize];
        long remaining = length;
        while (remaining > 0)
        {
            int count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
            if (count == 0)
            {
                throw new EndOfStreamException();
            }

            await destination.WriteAsync(buffer.AsMemory(0, count));
            remaining -= count;
        }

        await destination.FlushAsync();
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

    private static long GetOffset(int operation, int blockSize, long fileSize)
    {
        ulong blockCount = checked((ulong)(fileSize / blockSize));
        ulong mixed = unchecked((ulong)operation * 11400714819323198485UL + 0x54465142UL);
        return checked((long)(mixed % blockCount) * blockSize);
    }

    private static byte[][] CreateBuffers(int count, int size, bool fill)
    {
        byte[][] buffers = Enumerable.Range(0, count).Select(_ => new byte[size]).ToArray();
        if (fill)
        {
            var random = new Random(0x42514D54);
            foreach (byte[] buffer in buffers)
            {
                random.NextBytes(buffer);
            }
        }

        return buffers;
    }
}
