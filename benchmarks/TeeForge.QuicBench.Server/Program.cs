using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Runtime.Versioning;
using TeeForge.Networking;

namespace TeeForge.QuicBench.Server;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal static class Program
{
    private static readonly SslApplicationProtocol Protocol = new("teeforge-quic-benchmark-v1");

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var arguments = new BenchmarkArguments(args);
            if (StringComparer.OrdinalIgnoreCase.Equals(arguments.Get("storage", "file"), "memory"))
            {
                return await MemoryBenchmarkServer.RunAsync(arguments);
            }

            int port = arguments.GetInt32("port", 45678);
            long fileSize = arguments.GetInt32("file-size-mib", 64) * 1024L * 1024L;
            string workDirectory = Path.GetFullPath(arguments.Required("work-dir"));
            string sourcePath = Path.Combine(workDirectory, BenchmarkFiles.SourceName);
            string randomWritePath = Path.Combine(workDirectory, BenchmarkFiles.RemoteRandomWriteName);
            Directory.CreateDirectory(workDirectory);
            await BenchmarkFiles.EnsureSourceAsync(sourcePath, fileSize, CancellationToken.None);

            await using FileStream randomReadFile = BenchmarkFiles.OpenRandomAccessFile(
                sourcePath,
                FileAccess.Read,
                fileSize);
            await using FileStream randomWriteFile = BenchmarkFiles.OpenRandomAccessFile(
                randomWritePath,
                FileAccess.ReadWrite,
                fileSize);
            var options = new MutualQuicConnectionOptions(
                arguments.Required("certificate"),
                arguments.Required("private-key"),
                arguments.Required("trusted-peer-certificate"),
                Protocol,
                maximumInboundBidirectionalStreams: 512,
                maximumPendingNamedStreams: 64,
                maximumRandomAccessRequestSize: 1024 * 1024,
                maximumRandomAccessSessions: 16);
            await using MutualQuicConnectionListener listener =
                await MutualQuicConnectionListener.ListenAsync(
                    new IPEndPoint(IPAddress.Loopback, port),
                    options);
            Console.WriteLine($"READY {listener.LocalEndPoint.Port}");
            Console.Out.Flush();

            await using MutualQuicConnection connection = await listener.AcceptConnectionAsync();
            connection.RegisterRandomAccess(
                "random-read",
                new BenchmarkFileRandomAccess(randomReadFile));
            connection.RegisterRandomAccess(
                "random-write",
                new BenchmarkFileRandomAccess(randomWriteFile));
            await using (NamedQuicStream ready = await connection.OpenStreamAsync("benchmark-ready"))
            {
                await ready.WriteAsync(new byte[] { 1 });
                ready.CompleteWrites();
            }

            var handlers = new HashSet<Task>();
            bool shutdown = false;
            while (!shutdown)
            {
                NamedQuicStream stream = await connection.AcceptStreamAsync();
                if (stream.Name.StartsWith("shutdown-", StringComparison.Ordinal))
                {
                    await using (stream.ConfigureAwait(false))
                    {
                        await stream.WriteAsync(new byte[] { 0 });
                        stream.CompleteWrites();
                    }

                    shutdown = true;
                    continue;
                }

                Task handler = HandleSequentialStreamAsync(stream, sourcePath, workDirectory, fileSize);
                lock (handlers)
                {
                    handlers.Add(handler);
                }
                _ = handler.ContinueWith(
                    static (completed, state) =>
                    {
                        var set = (HashSet<Task>)state!;
                        lock (set)
                        {
                            set.Remove(completed);
                        }
                    },
                    handlers,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            Task[] remaining;
            lock (handlers)
            {
                remaining = handlers.ToArray();
            }

            await Task.WhenAll(remaining);
            await randomWriteFile.FlushAsync();
            Console.WriteLine("STOPPED");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task HandleSequentialStreamAsync(
        NamedQuicStream stream,
        string sourcePath,
        string workDirectory,
        long maximumLength)
    {
        await using (stream.ConfigureAwait(false))
        {
            byte[] request = new byte[12];
            await ReadExactlyAsync(stream, request).ConfigureAwait(false);
            long length = BinaryPrimitives.ReadInt64LittleEndian(request);
            int blockSize = BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(8));
            if (length < 0 || length > maximumLength || blockSize <= 0 || blockSize > 1024 * 1024)
            {
                throw new InvalidDataException("The sequential benchmark request is invalid.");
            }

            if (stream.Name.StartsWith("sequential-read-", StringComparison.Ordinal))
            {
                await SendFileAsync(stream, sourcePath, length, blockSize).ConfigureAwait(false);
                stream.CompleteWrites();
                return;
            }

            if (stream.Name.StartsWith("sequential-write-", StringComparison.Ordinal))
            {
                string outputPath = Path.Combine(workDirectory, BenchmarkFiles.RemoteSequentialWriteName);
                await ReceiveFileAsync(stream, outputPath, length, blockSize).ConfigureAwait(false);
                await stream.WriteAsync(new byte[] { 0 }).ConfigureAwait(false);
                stream.CompleteWrites();
                return;
            }

            throw new InvalidDataException($"Unknown benchmark stream '{stream.Name}'.");
        }
    }

    private static async Task SendFileAsync(
        Stream destination,
        string path,
        long length,
        int blockSize)
    {
        await using var source = new FileStream(
            path,
            new FileStreamOptions
            {
                Access = FileAccess.Read,
                BufferSize = blockSize,
                Mode = FileMode.Open,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                Share = FileShare.ReadWrite,
            });
        byte[] buffer = new byte[blockSize];
        long remaining = length;
        while (remaining > 0)
        {
            int count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)))
                .ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException();
            }

            await destination.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
            remaining -= count;
        }

        await destination.FlushAsync().ConfigureAwait(false);
    }

    private static async Task ReceiveFileAsync(
        Stream source,
        string path,
        long length,
        int blockSize)
    {
        await using var destination = new FileStream(
            path,
            new FileStreamOptions
            {
                Access = FileAccess.Write,
                BufferSize = blockSize,
                Mode = FileMode.Create,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                Share = FileShare.Read,
            });
        byte[] buffer = new byte[blockSize];
        long remaining = length;
        while (remaining > 0)
        {
            int count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)))
                .ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException();
            }

            await destination.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
            remaining -= count;
        }

        await destination.FlushAsync().ConfigureAwait(false);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int count = await stream.ReadAsync(buffer[offset..]).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException();
            }

            offset += count;
        }
    }
}
