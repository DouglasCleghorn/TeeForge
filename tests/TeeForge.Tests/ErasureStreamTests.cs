using TeeForge.ErasureCoding;

namespace TeeForge.Tests;

public class ErasureStreamTests
{
    private const int DataCount = 4;
    private const int ParityCount = 2;
    private const int BlockSize = 4096;
    private const long Length = 3L * DataCount * BlockSize + 777;

    [Fact]
    public async Task Self_describing_set_round_trips_sequential_and_random_io()
    {
        MemoryStream[] members = CreateMembers(DataCount + ParityCount);
        var options = new ErasureStreamOptions(leaveOpen: true, readAheadBlockCount: 2);
        byte[] expected = CreatePayload(checked((int)Length));

        await using (ErasureStream stream = ErasureStream.Create(
            members, DataCount, ParityCount, Length, BlockSize, options))
        {
            await stream.WriteAsync(expected);
            stream.Position = 0;
            var sequential = new byte[expected.Length];
            await stream.ReadExactlyAsync(sequential);
            Assert.Equal(expected, sequential);

            byte[] patch = CreatePayload(7000, seed: 43);
            await stream.WriteAtAsync(patch, BlockSize - 137);
            patch.CopyTo(expected, BlockSize - 137);

            var random = new byte[patch.Length];
            Assert.Equal(random.Length, await stream.ReadAtAsync(random, BlockSize - 137));
            Assert.Equal(patch, random);
        }

        Stream[] reordered = [members[5], members[0], members[3], members[1], members[4], members[2]];
        await using ErasureStream reopened = ErasureStream.Open(reordered, options);
        var actual = new byte[expected.Length];
        Assert.Equal(actual.Length, await reopened.ReadAtAsync(actual, 0));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Public_parser_exposes_aligned_geometry_and_ordered_member_ids()
    {
        MemoryStream[] members = CreateMembers(DataCount + ParityCount);
        var options = new ErasureStreamOptions(leaveOpen: true);
        using ErasureStream stream = ErasureStream.Create(
            members, DataCount, ParityCount, Length, BlockSize, options);

        ErasureStreamHeader[] headers = members.Select(ErasureStreamHeaderParser.Read).ToArray();
        ErasureStreamHeader parsedBytes = ErasureStreamHeaderParser.Parse(members[1].ToArray().AsSpan(0, 4096));
        Assert.Equal(headers[1].MemberId, parsedBytes.MemberId);
        Assert.All(headers, header =>
        {
            Assert.Equal(DataCount, header.DataShardCount);
            Assert.Equal(ParityCount, header.ParityShardCount);
            Assert.Equal((uint)BlockSize, header.BlockSize);
            Assert.Equal(0UL, header.DataOffset % header.DataAlignment);
            Assert.Equal(headers[0].SetId, header.SetId);
            Assert.Equal(headers[0].MemberIds, header.MemberIds);
        });
        Assert.Equal(Enumerable.Range(0, headers.Length), headers.Select(header => (int)header.MemberPosition));
        Assert.Equal(headers.Select(header => header.MemberId), headers[0].MemberIds);

        members[0].Position = 0;
        members[0].WriteByte(0xff);
        ErasureStreamHeader recovered = ErasureStreamHeaderParser.Read(members[0]);
        Assert.Equal(headers[0].MemberId, recovered.MemberId);
    }

    [Fact]
    public async Task Raw_set_has_no_header_and_round_trips()
    {
        MemoryStream[] members = CreateMembers(DataCount + ParityCount);
        var options = new ErasureStreamOptions(ErasureStreamFormat.Raw, leaveOpen: true);
        byte[] payload = CreatePayload(20_000);
        await using (ErasureStream stream = ErasureStream.Create(
            members, DataCount, ParityCount, Length, BlockSize, options))
        {
            await stream.WriteAtAsync(payload, 321);
        }

        Assert.False(ErasureStreamHeaderParser.TryRead(members[0], out _));
        await using ErasureStream reopened = ErasureStream.OpenRaw(
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

        await using ErasureStream reopened = ErasureStream.Open(storage, options);
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

        Stream[] survivors = [members[1], members[2], members[3], members[4], members[5]];
        Assert.Throws<InvalidDataException>(() => ErasureStream.Open(survivors, createOptions));

        var degradedOptions = new ErasureStreamOptions(requireAllMembers: false, leaveOpen: true);
        await using ErasureStream degraded = ErasureStream.Open(survivors, degradedOptions);
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

        Stream[] survivors = [members[1], members[2], members[3], members[4], members[5]];
        var degradedOptions = new ErasureStreamOptions(requireAllMembers: false, leaveOpen: true);
        await using ErasureStream degraded = ErasureStream.Open(survivors, degradedOptions);
        var actual = new byte[expected.Length];
        await degraded.ReadAtAsync(actual, 0);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Missing_parity_image_can_be_replaced_and_reopened()
    {
        MemoryStream[] members = CreateMembers(DataCount + ParityCount);
        byte[] payload = CreatePayload(checked((int)Length));
        var options = new ErasureStreamOptions(leaveOpen: true);
        await using (ErasureStream stream = ErasureStream.Create(
            members, DataCount, ParityCount, Length, BlockSize, options))
        {
            await stream.WriteAtAsync(payload, 0);
        }

        Stream[] survivors = [members[0], members[1], members[2], members[3], members[5]];
        var degradedOptions = new ErasureStreamOptions(requireAllMembers: false, leaveOpen: true);
        var replacement = new MemoryStream();
        await using (ErasureStream degraded = ErasureStream.Open(survivors, degradedOptions))
        {
            Assert.False(degraded.CanWrite);
            await degraded.ReplaceParityImageAsync(0, replacement);
            Assert.True(degraded.CanWrite);
            Assert.Empty(degraded.MissingMemberPositions);
        }

        Stream[] restored = [members[0], members[1], members[2], members[3], replacement, members[5]];
        await using ErasureStream reopened = ErasureStream.Open(restored, options);
        var actual = new byte[payload.Length];
        await reopened.ReadAtAsync(actual, 0);
        Assert.Equal(payload, actual);
    }

    [Fact]
    public async Task Parity_can_be_added_and_trailing_parity_can_be_removed()
    {
        MemoryStream[] members = CreateMembers(DataCount + 1);
        byte[] payload = CreatePayload(checked((int)Length));
        var options = new ErasureStreamOptions(leaveOpen: true);
        var added = new MemoryStream();
        await using (ErasureStream stream = ErasureStream.Create(
            members, DataCount, 1, Length, BlockSize, options))
        {
            await stream.WriteAtAsync(payload, 0);
            await stream.IncreaseParityAsync(added);
            Assert.Equal(2, stream.ParityShardCount);
        }

        Stream[] addedParitySurvivors = [members[1], members[2], members[3], added];
        var degradedOptions = new ErasureStreamOptions(requireAllMembers: false, leaveOpen: true);
        await using (ErasureStream degraded = ErasureStream.Open(addedParitySurvivors, degradedOptions))
        {
            var reconstructed = new byte[payload.Length];
            await degraded.ReadAtAsync(reconstructed, 0);
            Assert.Equal(payload, reconstructed);
        }

        Stream[] expandedMembers = [.. members, added];
        await using (ErasureStream expanded = ErasureStream.Open(expandedMembers, options))
        {
            IReadOnlyList<Stream> removed = await expanded.ReduceParityAsync(1);
            Assert.Same(added, Assert.Single(removed));
        }

        await using ErasureStream reopened = ErasureStream.Open(members, options);
        var actual = new byte[payload.Length];
        await reopened.ReadAtAsync(actual, 0);
        Assert.Equal(payload, actual);
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
