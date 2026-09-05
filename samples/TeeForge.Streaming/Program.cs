using TeeForge.ErasureCoding;

const int dataCount = 4;
const int parityCount = 2;
const int blockSize = 64 * 1024;
const long logicalLength = 16L * 1024 * 1024 + 777;
string directory = Path.Combine("artifacts", "streaming", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
string[] paths = Enumerable.Range(0, dataCount + parityCount)
    .Select(index => Path.Combine(directory, $"member-{index}.bin")).ToArray();

Stream[] outputs = paths.Select(path => (Stream)new ForwardOnlyStream(new FileStream(
    path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
    FileOptions.Asynchronous | FileOptions.SequentialScan))).ToArray();

await using (var input = new PatternStream(logicalLength))
await using (ErasureStream encoded = ErasureStream.Create(
    outputs, dataCount, parityCount, logicalLength, blockSize,
    new ErasureStreamOptions(maximumCacheBytes: (dataCount + parityCount) * blockSize, readAheadBlockCount: 0)))
{
    if (encoded.CanSeek)
    {
        throw new InvalidOperationException("This example must exercise forward-only encoding.");
    }

    await input.CopyToAsync(encoded);
    await encoded.CompleteAsync();
}

// The caller retains these geometry values and the member order. No metadata
// is inserted into the member payloads by TeeForge.
foreach (bool degraded in new[] { false, true })
{
    Stream?[] inputs = paths.Select((path, index) => degraded && index is 0 or 4
        ? null
        : (Stream)new ForwardOnlyStream(new FileStream(path, FileMode.Open,
            FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))).ToArray();
    await using ErasureStream decoded = ErasureStream.Open(
        inputs, dataCount, parityCount, logicalLength, blockSize,
        new ErasureStreamOptions(requireAllMembers: !degraded,
            maximumCacheBytes: (dataCount + parityCount) * blockSize, readAheadBlockCount: 0));
    await using var verified = new PatternVerifier();
    await decoded.CopyToAsync(verified);
    if (verified.BytesVerified != logicalLength)
    {
        throw new InvalidDataException("Decoded length does not match the declared length.");
    }

    Console.WriteLine($"Verified {verified.BytesVerified:N0} bytes with {(degraded ? "two missing members" : "all six members")}.");
}

Console.WriteLine($"Member files retained in {Path.GetFullPath(directory)}");

internal sealed class ForwardOnlyStream(Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        GC.SuppressFinalize(this);
        await base.DisposeAsync();
    }
}

internal sealed class PatternStream(long length) : Stream
{
    private long _offset;
    public override bool CanRead => true;
    public override bool CanWrite => false;
    public override bool CanSeek => false;
    public override long Length => length;
    public override long Position { get => _offset; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        int count = (int)Math.Min(buffer.Length, length - _offset);
        for (int index = 0; index < count; index++) buffer[index] = (byte)((_offset + index) % 251);
        _offset += count;
        return count;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }
    public override void Flush() { }
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

internal sealed class PatternVerifier : Stream
{
    internal long BytesVerified { get; private set; }
    public override bool CanRead => false;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        for (int index = 0; index < buffer.Length; index++)
        {
            if (buffer[index] != (byte)((BytesVerified + index) % 251))
                throw new InvalidDataException($"Incorrect decoded byte at {BytesVerified + index}.");
        }
        BytesVerified += buffer.Length;
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
