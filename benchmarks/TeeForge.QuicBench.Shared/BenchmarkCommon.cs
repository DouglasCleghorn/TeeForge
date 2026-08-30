using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using TeeForge.RandomAccess;

namespace TeeForge.QuicBench;

internal sealed class BenchmarkArguments
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    internal BenchmarkArguments(string[] args)
    {
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
            {
                throw new ArgumentException($"Expected '--name value', got '{argument}'.");
            }

            _values.Add(argument[2..], args[++index]);
        }
    }

    internal string Required(string name) =>
        _values.TryGetValue(name, out string? value)
            ? value
            : throw new ArgumentException($"Missing required option '--{name}'.");

    internal string Get(string name, string defaultValue) =>
        _values.TryGetValue(name, out string? value) ? value : defaultValue;

    internal bool Contains(string name) => _values.ContainsKey(name);

    internal int GetInt32(string name, int defaultValue)
    {
        string text = Get(name, defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return int.TryParse(text, out int value) && value > 0
            ? value
            : throw new ArgumentException($"Option '--{name}' must be a positive integer.");
    }

    internal int[] GetInt32List(string name, string defaultValue) =>
        Get(name, defaultValue)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(text => int.TryParse(text, out int value) && value > 0
                ? value
                : throw new ArgumentException($"Option '--{name}' contains invalid value '{text}'."))
            .Distinct()
            .Order()
            .ToArray();
}

internal static class BenchmarkFiles
{
    internal const string SourceName = "source.bin";
    internal const string RemoteSequentialWriteName = "remote-sequential-write.bin";
    internal const string DirectSequentialWriteName = "direct-sequential-write.bin";
    internal const string RemoteRandomWriteName = "remote-random-write.bin";
    internal const string DirectRandomWriteName = "direct-random-write.bin";

    internal static async Task EnsureSourceAsync(
        string path,
        long length,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path) && new FileInfo(path).Length == length)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Access = FileAccess.Write,
                BufferSize = 1024 * 1024,
                Mode = FileMode.Create,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                Share = FileShare.Read,
            });
        byte[] buffer = new byte[1024 * 1024];
        var random = new Random(0x54465142);
        long remaining = length;
        while (remaining > 0)
        {
            random.NextBytes(buffer);
            int count = (int)Math.Min(buffer.Length, remaining);
            await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            remaining -= count;
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static FileStream OpenRandomAccessFile(string path, FileAccess access, long length)
    {
        var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Access = access,
                BufferSize = 1,
                Mode = access == FileAccess.Read ? FileMode.Open : FileMode.OpenOrCreate,
                Options = FileOptions.Asynchronous | FileOptions.RandomAccess,
                Share = FileShare.ReadWrite,
            });
        if (access != FileAccess.Read && stream.Length != length)
        {
            stream.SetLength(length);
        }

        return stream;
    }
}

internal sealed class BenchmarkFileRandomAccess : ITeeRandomAccessStream
{
    private readonly SafeFileHandle _handle;

    internal BenchmarkFileRandomAccess(FileStream stream)
    {
        _handle = stream.SafeFileHandle;
        CanReadAt = stream.CanRead;
        CanWriteAt = stream.CanWrite;
    }

    public bool CanReadAt { get; }

    public bool CanWriteAt { get; }

    public int ReadAt(Span<byte> buffer, long offset)
    {
        if (!CanReadAt)
        {
            throw new NotSupportedException();
        }

        return System.IO.RandomAccess.Read(_handle, buffer, offset);
    }

    public ValueTask<int> ReadAtAsync(
        Memory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default)
    {
        if (!CanReadAt)
        {
            throw new NotSupportedException();
        }

        return System.IO.RandomAccess.ReadAsync(_handle, buffer, offset, cancellationToken);
    }

    public void WriteAt(ReadOnlySpan<byte> buffer, long offset)
    {
        if (!CanWriteAt)
        {
            throw new NotSupportedException();
        }

        System.IO.RandomAccess.Write(_handle, buffer, offset);
    }

    public ValueTask WriteAtAsync(
        ReadOnlyMemory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default)
    {
        if (!CanWriteAt)
        {
            throw new NotSupportedException();
        }

        return System.IO.RandomAccess.WriteAsync(_handle, buffer, offset, cancellationToken);
    }
}

internal static class BenchmarkCertificates
{
    internal static void CreatePair(string directory)
    {
        Directory.CreateDirectory(directory);
        CreateIdentity(directory, "server", includeLocalhostSan: true);
        CreateIdentity(directory, "client", includeLocalhostSan: false);
    }

    private static void CreateIdentity(string directory, string name, bool includeLocalhostSan)
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={name}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [
                    new Oid("1.3.6.1.5.5.7.3.1"),
                    new Oid("1.3.6.1.5.5.7.3.2"),
                ],
                true));
        if (includeLocalhostSan)
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
        }

        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(30));
        File.WriteAllText(Path.Combine(directory, $"{name}.crt.pem"), certificate.ExportCertificatePem());
        File.WriteAllText(Path.Combine(directory, $"{name}.key.pem"), rsa.ExportPkcs8PrivateKeyPem());
    }
}

internal sealed record BenchmarkResult(
    string Operation,
    string Path,
    int BlockSize,
    int QueueDepth,
    int Iteration,
    long Bytes,
    double ElapsedMilliseconds,
    double MebibytesPerSecond,
    double OperationsPerSecond);

internal sealed record BenchmarkRun(
    DateTimeOffset Timestamp,
    string Framework,
    string OperatingSystem,
    int ProcessorCount,
    long FileSize,
    long RandomBytesPerCase,
    int SequentialIterations,
    int RandomIterations,
    string Compression,
    int RandomCompressionThreshold,
    IReadOnlyList<int> SequentialBlockSizes,
    IReadOnlyList<int> RandomBlockSizes,
    IReadOnlyList<int> QueueDepths,
    IReadOnlyList<BenchmarkResult> Results)
{
    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
    };
}

internal sealed record MemoryBenchmarkRun(
    DateTimeOffset Timestamp,
    string Framework,
    string OperatingSystem,
    int ProcessorCount,
    long LogicalSize,
    int SegmentSize,
    long RandomBytesPerCase,
    int SequentialIterations,
    int RandomIterations,
    string Compression,
    int RandomCompressionThreshold,
    IReadOnlyList<int> SequentialBlockSizes,
    IReadOnlyList<int> RandomBlockSizes,
    IReadOnlyList<int> QueueDepths,
    IReadOnlyList<BenchmarkResult> Results);
