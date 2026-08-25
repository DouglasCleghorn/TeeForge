using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TeeForge.Networking;
using TeeForge.RandomAccess;

namespace TeeForge.Tests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class MutualQuicConnectionTests
{
    private static readonly SslApplicationProtocol TestProtocol = new("teeforge-tests");

    [Fact]
    public void Options_RequireLocalFilesKeysAndValidCompression()
    {
        using var clientIdentity = new TestIdentity("client");
        using var serverIdentity = new TestIdentity("server");
        var options = new MutualQuicConnectionOptions(
            clientIdentity.CertificatePath,
            clientIdentity.PrivateKeyPath,
            serverIdentity.CertificatePath,
            TestProtocol);

        Assert.Equal(Path.GetFullPath(clientIdentity.CertificatePath), options.LocalCertificatePath);
        Assert.Equal(Path.GetFullPath(clientIdentity.PrivateKeyPath), options.LocalPrivateKeyPath);
        Assert.Equal(QuicStreamCompressionAlgorithms.All, options.AllowedCompressions);
        Assert.Throws<FileNotFoundException>(() =>
            new MutualQuicConnectionOptions(
                Path.Combine(clientIdentity.DirectoryPath, "missing.crt"),
                clientIdentity.PrivateKeyPath,
                serverIdentity.CertificatePath,
                TestProtocol));
        Assert.Throws<ArgumentException>(() =>
            new MutualQuicConnectionOptions(
                clientIdentity.CertificatePath,
                clientIdentity.PrivateKeyPath,
                serverIdentity.CertificatePath,
                default));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new QuicRandomAccessOptions(compressionThreshold: 0));

        var mismatchedKeyOptions = new MutualQuicConnectionOptions(
            clientIdentity.CertificatePath,
            serverIdentity.PrivateKeyPath,
            serverIdentity.CertificatePath,
            TestProtocol);
        Assert.ThrowsAny<CryptographicException>(() => mismatchedKeyOptions.LoadLocalCertificate());
    }

    [Fact]
    public async Task Protocol_UsesNameOnceAndCompressesOnlyAtThreshold()
    {
        const string name = "dynamic-name";
        byte[] opening = QuicProtocol.CreateOpeningMessage(
            QuicProtocol.NamedStreamKind,
            name,
            QuicStreamCompression.BrotliFastest);
        Assert.Equal(
            QuicProtocol.CommonHeaderSize + 2 + name.Length,
            opening.Length);
        Assert.Equal((byte)name.Length, opening[QuicProtocol.CommonHeaderSize + 1]);

        byte[] small = Enumerable.Repeat((byte)'A', 1023).ToArray();
        (byte smallFlags, byte[] smallPayload) =
            await MutualQuicConnection.EncodePayloadAsync(
                small,
                QuicStreamCompression.BrotliFastest,
                threshold: 1024,
                CancellationToken.None);
        Assert.Equal(0, smallFlags);
        Assert.Equal(small, smallPayload);

        byte[] large = Enumerable.Repeat((byte)'B', 4096).ToArray();
        (byte largeFlags, byte[] largePayload) =
            await MutualQuicConnection.EncodePayloadAsync(
                large,
                QuicStreamCompression.BrotliFastest,
                threshold: 1024,
                CancellationToken.None);
        Assert.Equal(QuicProtocol.CompressedFlag, largeFlags);
        Assert.True(largePayload.Length < large.Length);
        byte[] roundTrip = await MutualQuicConnection.DecompressAsync(
            largePayload,
            large.Length,
            CancellationToken.None);
        Assert.Equal(large, roundTrip);
    }

    [Fact]
    public async Task Connection_PinsBothCertificates()
    {
        SkipIfQuicIsUnavailable();
        await using ConnectedPair pair = await CreateConnectedPairAsync();
        using X509Certificate2 clientCertificate =
            X509CertificateLoader.LoadCertificateFromFile(pair.ClientIdentity.CertificatePath);
        using X509Certificate2 serverCertificate =
            X509CertificateLoader.LoadCertificateFromFile(pair.ServerIdentity.CertificatePath);

        Assert.True(HasSameCertificate(pair.Client.RemoteCertificate, serverCertificate));
        Assert.True(HasSameCertificate(pair.Server.RemoteCertificate, clientCertificate));
        Assert.True(pair.Client.IsClient);
        Assert.False(pair.Server.IsClient);
        Assert.Equal(TestProtocol, pair.Client.NegotiatedApplicationProtocol);
        Assert.Equal(TestProtocol, pair.Server.NegotiatedApplicationProtocol);
    }

    [Fact]
    public async Task BothEndpoints_OpenMultipleDynamicNamedStreams()
    {
        SkipIfQuicIsUnavailable();
        await using ConnectedPair pair = await CreateConnectedPairAsync();

        ValueTask<NamedQuicStream> serverAccept = pair.Server.AcceptStreamAsync();
        await using NamedQuicStream clientMetadata = await pair.Client.OpenStreamAsync("metadata");
        await using NamedQuicStream serverMetadata = await serverAccept;
        ValueTask<NamedQuicStream> clientAccept = pair.Client.AcceptStreamAsync();
        await using NamedQuicStream serverEvents = await pair.Server.OpenStreamAsync("events");
        await using NamedQuicStream clientEvents = await clientAccept;

        Assert.Equal("metadata", serverMetadata.Name);
        Assert.Equal("events", clientEvents.Name);
        Assert.Equal(clientMetadata.Id, serverMetadata.Id);
        Assert.Equal(serverEvents.Id, clientEvents.Id);
        Assert.NotEqual(clientMetadata.Id, serverEvents.Id);

        byte[] metadata = "metadata payload"u8.ToArray();
        byte[] events = "event payload"u8.ToArray();
        await Task.WhenAll(
            clientMetadata.WriteAsync(metadata).AsTask(),
            serverEvents.WriteAsync(events).AsTask());
        byte[] metadataRead = new byte[metadata.Length];
        byte[] eventsRead = new byte[events.Length];
        await Task.WhenAll(
            ReadExactlyAsync(serverMetadata, metadataRead),
            ReadExactlyAsync(clientEvents, eventsRead));
        Assert.Equal(metadata, metadataRead);
        Assert.Equal(events, eventsRead);
    }

    [Fact]
    public async Task NamedStream_RejectsActiveDuplicateAndReusesDisposedName()
    {
        SkipIfQuicIsUnavailable();
        await using ConnectedPair pair = await CreateConnectedPairAsync();
        ValueTask<NamedQuicStream> firstAccept = pair.Server.AcceptStreamAsync();
        NamedQuicStream firstClient = await pair.Client.OpenStreamAsync("shared");
        NamedQuicStream firstServer = await firstAccept;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pair.Client.OpenStreamAsync("shared"));
        await firstClient.DisposeAsync();
        await firstServer.DisposeAsync();

        ValueTask<NamedQuicStream> secondAccept = pair.Client.AcceptStreamAsync();
        await using NamedQuicStream secondServer = await pair.Server.OpenStreamAsync("shared");
        await using NamedQuicStream secondClient = await secondAccept;
        Assert.Equal("shared", secondClient.Name);
    }

    [Fact]
    public async Task SimultaneousNameCollision_ClientInitiatedStreamWins()
    {
        SkipIfQuicIsUnavailable();
        await using ConnectedPair pair = await CreateConnectedPairAsync();
        ValueTask<NamedQuicStream> serverAccept = pair.Server.AcceptStreamAsync();

        ValueTask<NamedQuicStream> clientOpening = pair.Client.OpenStreamAsync("collision");
        Task<NamedQuicStream> serverOpening = pair.Server.OpenStreamAsync("collision").AsTask();

        await using NamedQuicStream client = await clientOpening;
        await using NamedQuicStream server = await serverAccept;
        await Assert.ThrowsAnyAsync<Exception>(async () => await serverOpening);
        Assert.Equal("collision", client.Name);
        Assert.Equal(client.Id, server.Id);
    }

    [Fact]
    public async Task NamedStream_ProvidesTransparentDuplexBrotliAndPipeHalves()
    {
        SkipIfQuicIsUnavailable();
        await using ConnectedPair pair = await CreateConnectedPairAsync();
        ValueTask<NamedQuicStream> accept = pair.Server.AcceptStreamAsync();
        await using NamedQuicStream client = await pair.Client.OpenStreamAsync(
            "compressed",
            new NamedQuicStreamOptions(QuicStreamCompression.BrotliFastest));
        await using NamedQuicStream server = await accept;
        Assert.Equal(QuicStreamCompression.BrotliFastest, server.Compression);

        byte[] request = Enumerable.Repeat((byte)'A', 128 * 1024).ToArray();
        byte[] response = Enumerable.Repeat((byte)'B', 96 * 1024).ToArray();
        await Task.WhenAll(
            client.Output.WriteAsync(request).AsTask(),
            server.Output.WriteAsync(response).AsTask());
        await Task.WhenAll(
            client.Output.FlushAsync().AsTask(),
            server.Output.FlushAsync().AsTask());

        byte[] serverRead = await ReadPipeExactlyAsync(server.Input, request.Length);
        byte[] clientRead = await ReadPipeExactlyAsync(client.Input, response.Length);
        Assert.Equal(request, serverRead);
        Assert.Equal(response, clientRead);
    }

    [Fact]
    public async Task ReceiverCompressionPolicy_RejectsDisallowedSelection()
    {
        SkipIfQuicIsUnavailable();
        await using ConnectedPair pair = await CreateConnectedPairAsync(
            serverAllowedCompressions: QuicStreamCompressionAlgorithms.Uncompressed);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await pair.Client.OpenStreamAsync(
                "compressed",
                new NamedQuicStreamOptions(QuicStreamCompression.BrotliFastest)));
    }

    [Fact]
    public async Task RandomAccess_UsesNamedServiceAndThresholdCompression()
    {
        SkipIfQuicIsUnavailable();
        await using ConnectedPair pair = await CreateConnectedPairAsync();
        var backing = new TestRandomAccessStream(256 * 1024);
        pair.Server.RegisterRandomAccess("disk", backing);
        QuicRandomAccessChannel channel = await pair.Client.OpenRandomAccessAsync(
            "disk",
            new QuicRandomAccessOptions(
                QuicStreamCompression.BrotliFastest,
                compressionThreshold: 1024));

        Assert.Equal("disk", channel.Name);
        Assert.Equal(1024, channel.CompressionThreshold);
        Assert.True(channel.CanReadAt);
        Assert.True(channel.CanWriteAt);

        byte[] large = Enumerable.Repeat((byte)0x5A, 32 * 1024).ToArray();
        byte[] small = Enumerable.Range(0, 128).Select(value => (byte)value).ToArray();
        await Task.WhenAll(
            channel.WriteAtAsync(large, 4096).AsTask(),
            channel.WriteAtAsync(small, 96 * 1024).AsTask());
        byte[] largeRead = new byte[large.Length];
        byte[] smallRead = new byte[small.Length];
        int[] counts = await Task.WhenAll(
            channel.ReadAtAsync(largeRead, 4096).AsTask(),
            channel.ReadAtAsync(smallRead, 96 * 1024).AsTask());
        Assert.Equal([large.Length, small.Length], counts);
        Assert.Equal(large, largeRead);
        Assert.Equal(small, smallRead);
    }

    private static async ValueTask<ConnectedPair> CreateConnectedPairAsync(
        QuicStreamCompressionAlgorithms serverAllowedCompressions =
            QuicStreamCompressionAlgorithms.All)
    {
        var clientIdentity = new TestIdentity("client");
        var serverIdentity = new TestIdentity("server");
        var clientOptions = new MutualQuicConnectionOptions(
            clientIdentity.CertificatePath,
            clientIdentity.PrivateKeyPath,
            serverIdentity.CertificatePath,
            TestProtocol);
        var serverOptions = new MutualQuicConnectionOptions(
            serverIdentity.CertificatePath,
            serverIdentity.PrivateKeyPath,
            clientIdentity.CertificatePath,
            TestProtocol,
            allowedCompressions: serverAllowedCompressions);
        var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        MutualQuicConnectionListener? listener = null;

        try
        {
            listener = await MutualQuicConnectionListener.ListenAsync(
                new IPEndPoint(IPAddress.Loopback, 0),
                serverOptions,
                timeout.Token);
            ValueTask<MutualQuicConnection> accept = listener.AcceptConnectionAsync(timeout.Token);
            MutualQuicConnection client = await MutualQuicConnection.ConnectAsync(
                listener.LocalEndPoint,
                "localhost",
                clientOptions,
                timeout.Token);
            MutualQuicConnection server = await accept;
            return new ConnectedPair(
                client,
                server,
                listener,
                clientIdentity,
                serverIdentity,
                timeout);
        }
        catch (CryptographicException exception)
            when (OperatingSystem.IsWindows() &&
                exception.Message.Contains("file specified", StringComparison.OrdinalIgnoreCase))
        {
            if (listener is not null)
            {
                await listener.DisposeAsync();
            }

            clientIdentity.Dispose();
            serverIdentity.Dispose();
            timeout.Dispose();
            Assert.Skip("The Windows test sandbox does not permit a temporary persisted TLS key.");
            throw;
        }
        catch
        {
            if (listener is not null)
            {
                await listener.DisposeAsync();
            }

            clientIdentity.Dispose();
            serverIdentity.Dispose();
            timeout.Dispose();
            throw;
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..]);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }

    private static async Task<byte[]> ReadPipeExactlyAsync(PipeReader reader, int length)
    {
        byte[] result = new byte[length];
        int offset = 0;
        while (offset < result.Length)
        {
            ReadResult read = await reader.ReadAsync();
            ReadOnlySequence<byte> buffer = read.Buffer;
            int count = (int)Math.Min(buffer.Length, result.Length - offset);
            buffer.Slice(0, count).CopyTo(result.AsSpan(offset));
            SequencePosition consumed = buffer.GetPosition(count);
            reader.AdvanceTo(consumed, buffer.End);
            offset += count;
            if (read.IsCompleted && count == 0)
            {
                throw new EndOfStreamException();
            }
        }

        return result;
    }

    private static bool HasSameCertificate(X509Certificate? actual, X509Certificate expected) =>
        actual is not null &&
        actual.GetCertHash(HashAlgorithmName.SHA256)
            .AsSpan()
            .SequenceEqual(expected.GetCertHash(HashAlgorithmName.SHA256));

    private static void SkipIfQuicIsUnavailable() =>
        Assert.SkipWhen(
            !MutualQuicConnection.IsSupported || !MutualQuicConnectionListener.IsSupported,
            "System.Net.Quic is not supported on this machine.");

    private sealed class TestIdentity : IDisposable
    {
        internal TestIdentity(string commonName)
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "TeeForge.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            CertificatePath = Path.Combine(DirectoryPath, $"{commonName}.crt.pem");
            PrivateKeyPath = Path.Combine(DirectoryPath, $"{commonName}.key.pem");

            using RSA rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                $"CN={commonName}",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    [
                        new Oid("1.3.6.1.5.5.7.3.1"),
                        new Oid("1.3.6.1.5.5.7.3.2"),
                    ],
                    true));
            var subjectAlternativeName = new SubjectAlternativeNameBuilder();
            subjectAlternativeName.AddDnsName("localhost");
            request.CertificateExtensions.Add(subjectAlternativeName.Build());

            using X509Certificate2 certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllText(CertificatePath, certificate.ExportCertificatePem());
            File.WriteAllText(PrivateKeyPath, rsa.ExportPkcs8PrivateKeyPem());
        }

        internal string DirectoryPath { get; }

        internal string CertificatePath { get; }

        internal string PrivateKeyPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }

    private sealed class ConnectedPair : IAsyncDisposable
    {
        private readonly MutualQuicConnectionListener _listener;
        private readonly CancellationTokenSource _timeout;

        internal ConnectedPair(
            MutualQuicConnection client,
            MutualQuicConnection server,
            MutualQuicConnectionListener listener,
            TestIdentity clientIdentity,
            TestIdentity serverIdentity,
            CancellationTokenSource timeout)
        {
            Client = client;
            Server = server;
            _listener = listener;
            ClientIdentity = clientIdentity;
            ServerIdentity = serverIdentity;
            _timeout = timeout;
        }

        internal MutualQuicConnection Client { get; }

        internal MutualQuicConnection Server { get; }

        internal TestIdentity ClientIdentity { get; }

        internal TestIdentity ServerIdentity { get; }

        public async ValueTask DisposeAsync()
        {
            await Task.WhenAll(
                Client.DisposeAsync().AsTask(),
                Server.DisposeAsync().AsTask());
            await _listener.DisposeAsync();
            ClientIdentity.Dispose();
            ServerIdentity.Dispose();
            _timeout.Dispose();
        }
    }

    private sealed class TestRandomAccessStream : ITeeRandomAccessStream
    {
        private readonly byte[] _bytes;
        private readonly object _sync = new();

        internal TestRandomAccessStream(int length) => _bytes = new byte[length];

        public bool CanReadAt => true;

        public bool CanWriteAt => true;

        public int ReadAt(Span<byte> buffer, long offset)
        {
            lock (_sync)
            {
                if (offset >= _bytes.Length)
                {
                    return 0;
                }

                int count = Math.Min(buffer.Length, _bytes.Length - checked((int)offset));
                _bytes.AsSpan((int)offset, count).CopyTo(buffer);
                return count;
            }
        }

        public ValueTask<int> ReadAtAsync(
            Memory<byte> buffer,
            long offset,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReadAt(buffer.Span, offset));
        }

        public void WriteAt(ReadOnlySpan<byte> buffer, long offset)
        {
            lock (_sync)
            {
                buffer.CopyTo(_bytes.AsSpan(checked((int)offset), buffer.Length));
            }
        }

        public ValueTask WriteAtAsync(
            ReadOnlyMemory<byte> buffer,
            long offset,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteAt(buffer.Span, offset);
            return ValueTask.CompletedTask;
        }
    }
}
