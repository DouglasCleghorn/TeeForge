using TeeForge.ErasureCoding;

namespace TeeForge.Tests;

public class ErasureStreamTests
{
    private const int DataCount = 4;
    private const int ParityCount = 2;
    private const int BlockSize = 4096;
    private const long Length = 3L * DataCount * BlockSize + 777;

    [Fact]
    public async Task Raw_set_has_no_header_and_round_trips()
    {
        MemoryStream[] members = CreateMembers(DataCount + ParityCount);
        var options = new ErasureStreamOptions(leaveOpen: true);
        byte[] payload = CreatePayload(20_000);
        await using (ErasureStream stream = ErasureStream.Create(
            members, DataCount, ParityCount, Length, BlockSize, options))
        {
            await stream.WriteAtAsync(payload, 321);
        }

        Assert.Equal(payload[0], members[0].ToArray()[321]);
        await using ErasureStream reopened = ErasureStream.Open(
            members, DataCount, ParityCount, Length, BlockSize, options);
        var actual = new byte[payload.Length];
        await reopened.ReadAtAsync(actual, 321);
        Assert.Equal(payload, actual);
    }

    [Fact]
    public async Task Forward_only_members_encode_a_partial_final_codeword()
    {
        MemoryStream[] storage = CreateMembers(DataCount + ParityCount);
        Stream[] forward = storage.Select(static member => new ForwardWriteStream(member)).ToArray();
        byte[] payload = CreatePayload(checked((int)Length));
        var options = new ErasureStreamOptions(leaveOpen: true, maximumCacheBytes: BlockSize);
        await using (ErasureStream stream = ErasureStream.Create(
            forward, DataCount, ParityCount, Length, BlockSize, options))
        {
            Assert.False(stream.CanSeek);
            for (int offset = 0; offset < payload.Length; offset += 3001)
            {
                int count = Math.Min(3001, payload.Length - offset);
                await stream.WriteAsync(payload.AsMemory(offset, count));
            }

            await stream.CompleteAsync();
        }

        await using ErasureStream reopened = ErasureStream.Open(storage, DataCount, ParityCount, Length, BlockSize, options);
        var actual = new byte[payload.Length];
        await reopened.ReadAtAsync(actual, 0);
        Assert.Equal(payload, actual);
    }

    [Fact]
    public async Task Degraded_open_is_read_only_and_reconstructs_a_missing_data_member()
    {
        MemoryStream[] members = CreateMembers(DataCount + ParityCount);
        byte[] payload = CreatePayload(checked((int)Length));
        var createOptions = new ErasureStreamOptions(leaveOpen: true);
        await using (ErasureStream stream = ErasureStream.Create(
            members, DataCount, ParityCount, Length, BlockSize, createOptions))
        {
            await stream.WriteAtAsync(payload, 0);
        }

        Stream?[] survivors = [null, members[1], members[2], members[3], members[4], members[5]];
        Assert.Throws<InvalidDataException>(() => ErasureStream.Open(survivors, DataCount, ParityCount, Length, BlockSize, createOptions));

        var degradedOptions = new ErasureStreamOptions(requireAllMembers: false, leaveOpen: true);
        await using ErasureStream degraded = ErasureStream.Open(survivors, DataCount, ParityCount, Length, BlockSize, degradedOptions);
        Assert.True(degraded.CanRead);
        Assert.False(degraded.CanWrite);
        Assert.Equal([0], degraded.MissingMemberPositions);
        var actual = new byte[payload.Length];
        await degraded.ReadAtAsync(actual, 0);
        Assert.Equal(payload, actual);
    }

    [Fact]
    public async Task Concurrent_writes_to_one_codeword_are_serialized_and_keep_parity_valid()
    {
        MemoryStream[] members = CreateMembers(DataCount + ParityCount);
        var options = new ErasureStreamOptions(leaveOpen: true);
        var expected = new byte[DataCount * BlockSize];
        await using (ErasureStream stream = ErasureStream.Create(
            members, DataCount, ParityCount, Length, BlockSize, options))
        {
            Task[] writes = Enumerable.Range(0, 32).Select(index =>
            {
                byte[] patch = Enumerable.Repeat(checked((byte)(index + 1)), 256).ToArray();
                patch.CopyTo(expected, index * patch.Length);
                return stream.WriteAtAsync(patch, index * patch.Length).AsTask();
            }).ToArray();
            await Task.WhenAll(writes);
        }

        Stream?[] survivors = [null, members[1], members[2], members[3], members[4], members[5]];
        var degradedOptions = new ErasureStreamOptions(requireAllMembers: false, leaveOpen: true);
        await using ErasureStream degraded = ErasureStream.Open(survivors, DataCount, ParityCount, Length, BlockSize, degradedOptions);
        var actual = new byte[expected.Length];
        await degraded.ReadAtAsync(actual, 0);
        Assert.Equal(expected, actual);
    }

    private static MemoryStream[] CreateMembers(int count) =>
        Enumerable.Range(0, count).Select(static _ => new MemoryStream()).ToArray();

    private static byte[] CreatePayload(int length, int seed = 17)
    {
        var result = new byte[length];
        new Random(seed).NextBytes(result);
        return result;
    }

    private sealed class ForwardWriteStream(Stream destination) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => destination.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            destination.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => destination.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => destination.Write(buffer);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            destination.WriteAsync(buffer, cancellationToken);
    }
}
