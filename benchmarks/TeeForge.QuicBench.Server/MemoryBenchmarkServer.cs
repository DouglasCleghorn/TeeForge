using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Runtime.Versioning;
using TeeForge.Networking;

namespace TeeForge.QuicBench.Server;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal static class MemoryBenchmarkServer
{
    private static readonly SslApplicationProtocol Protocol = new("teeforge-quic-memory-benchmark-v1");

    internal static async Task<int> RunAsync(BenchmarkArguments arguments)
    {
        int port = arguments.GetInt32("port", 45678);
        long length = arguments.Contains("memory-size-mib")
            ? arguments.GetInt32("memory-size-mib", 64) * 1024L * 1024L
            : arguments.GetInt32("memory-size-gib", 3) * 1024L * 1024L * 1024L;
        int segmentSize = checked(arguments.GetInt32("memory-segment-mib", 64) * 1024 * 1024);
        Console.WriteLine(
            $"Allocating {length / 1024d / 1024d / 1024d:F1} GiB across " +
            $"{segmentSize / 1024 / 1024} MiB MemoryStream segments...");
        using var store = new SegmentedMemoryStore(length, segmentSize);
        var transferOnly = new BenchmarkTransferRandomAccess(length);

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
        connection.RegisterRandomAccess("memory-random-read", store);
        connection.RegisterRandomAccess("memory-random-write", store);
        connection.RegisterRandomAccess("transfer-random-read", transferOnly);
        connection.RegisterRandomAccess("transfer-random-write", transferOnly);
        await using (NamedQuicStream ready = await connection.OpenStreamAsync("memory-benchmark-ready"))
        {
            await ready.WriteAsync(new byte[] { 1 });
            ready.CompleteWrites();
        }

        var handlers = new HashSet<Task>();
        bool shutdown = false;
        while (!shutdown)
        {
            NamedQuicStream stream = await connection.AcceptStreamAsync();
            if (stream.Name.StartsWith("memory-shutdown-", StringComparison.Ordinal))
            {
                await using (stream.ConfigureAwait(false))
                {
                    await stream.WriteAsync(new byte[] { 0 });
                    stream.CompleteWrites();
                }

                shutdown = true;
                continue;
            }

            Task handler = HandleStreamAsync(stream, store, length);
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
        Console.WriteLine("STOPPED");
        return 0;
    }

    private static async Task HandleStreamAsync(
        NamedQuicStream stream,
        SegmentedMemoryStore store,
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
                throw new InvalidDataException("The memory benchmark request is invalid.");
            }

            if (stream.Name.StartsWith("memory-read-", StringComparison.Ordinal))
            {
                using Stream source = store.OpenStream();
                await CopyExactlyAsync(source, stream, length, blockSize).ConfigureAwait(false);
                stream.CompleteWrites();
                return;
            }

            if (stream.Name.StartsWith("memory-write-", StringComparison.Ordinal))
            {
                using Stream destination = store.OpenStream();
                await CopyExactlyAsync(stream, destination, length, blockSize).ConfigureAwait(false);
                await stream.WriteAsync(new byte[] { 0 }).ConfigureAwait(false);
                stream.CompleteWrites();
                return;
            }

            if (stream.Name.StartsWith("transfer-read-", StringComparison.Ordinal))
            {
                await SendGeneratedAsync(stream, length, blockSize).ConfigureAwait(false);
                stream.CompleteWrites();
                return;
            }

            if (stream.Name.StartsWith("transfer-write-", StringComparison.Ordinal))
            {
                await ReceiveAndDiscardAsync(stream, length, blockSize).ConfigureAwait(false);
                await stream.WriteAsync(new byte[] { 0 }).ConfigureAwait(false);
                stream.CompleteWrites();
                return;
            }

            throw new InvalidDataException($"Unknown memory benchmark stream '{stream.Name}'.");
        }
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

    private static async Task SendGeneratedAsync(Stream destination, long length, int blockSize)
    {
        byte[] buffer = new byte[blockSize];
        new Random(0x51554943).NextBytes(buffer);
        long remaining = length;
        while (remaining > 0)
        {
            int count = (int)Math.Min(buffer.Length, remaining);
            await destination.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
            remaining -= count;
        }

        await destination.FlushAsync().ConfigureAwait(false);
    }

    private static async Task ReceiveAndDiscardAsync(Stream source, long length, int blockSize)
    {
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

            remaining -= count;
        }
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
