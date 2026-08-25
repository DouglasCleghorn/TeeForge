using TeeForge.ErasureCoding;
using TeeForge.ErasureCoding.Internal;

namespace TeeForge.Tests;

public class ErasureCodeStreamTests
{
    private const int DataShardCount = 3;
    private const int ParityShardCount = 2;
    private const int ShardSize = ErasureFormatV1.MinimumShardSize;
    private const long Capacity = 2L * DataShardCount * ShardSize;

    [Fact]
    public async Task Create_reads_implicit_zeroes_and_round_trips_cross_block_writes()
    {
        MemoryStream[] members = CreateEmptyMembers();
        var options = new ErasureCodeStreamOptions(leaveOpen: true);
        await using ErasureCodeStream stream = await ErasureCodeStream.CreateAsync(
            members,
            DataShardCount,
            ParityShardCount,
            Capacity,
            ShardSize,
            options);
        var initial = new byte[4096];
        Assert.Equal(initial.Length, await stream.ReadAtAsync(initial, 1234));
        Assert.All(initial, static value => Assert.Equal((byte)0, value));

        byte[] payload = CreatePayload(90_000);
        long offset = ShardSize - 10_000;
        await stream.WriteAtAsync(payload, offset);

        var actual = new byte[payload.Length];
        Assert.Equal(actual.Length, await stream.ReadAtAsync(actual, offset));
        Assert.Equal(payload, actual);
        var before = new byte[100];
        await stream.ReadAtAsync(before, offset - before.Length);
        Assert.All(before, static value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public async Task Reopen_accepts_members_in_any_order_and_recovers_data()
    {
        MemoryStream[] members = CreateEmptyMembers();
        var options = new ErasureCodeStreamOptions(leaveOpen: true);
        byte[] payload = CreatePayload(70_000);
        await using (ErasureCodeStream created = await ErasureCodeStream.CreateAsync(
            members,
            DataShardCount,
            ParityShardCount,
            Capacity,
            ShardSize,
            options))
        {
            await created.WriteAtAsync(payload, 17_000);
        }

        Stream[] reordered = [members[4], members[1], members[3], members[0], members[2]];
        await using ErasureCodeStream reopened = await ErasureCodeStream.OpenAsync(reordered, options);
        var actual = new byte[payload.Length];
        Assert.Equal(actual.Length, await reopened.ReadAtAsync(actual, 17_000));
        Assert.Equal(payload, actual);
    }

    [Fact]
    public async Task Degraded_open_reconstructs_a_missing_data_member()
    {
        MemoryStream[] members = CreateEmptyMembers();
        var options = new ErasureCodeStreamOptions(leaveOpen: true);
        byte[] payload = CreatePayload(ShardSize);
        await using (ErasureCodeStream created = await ErasureCodeStream.CreateAsync(
            members,
            DataShardCount,
            ParityShardCount,
            Capacity,
            ShardSize,
            options))
        {
            await created.WriteAtAsync(payload, 0);
        }

        Stream[] surviving = [members[1], members[2], members[3]];
        var readOnly = new ErasureCodeStreamOptions(leaveOpen: true, readOnly: true);
        await using ErasureCodeStream degraded = await ErasureCodeStream.OpenAsync(surviving, readOnly);
        var actual = new byte[payload.Length];
        Assert.Equal(actual.Length, await degraded.ReadAtAsync(actual, 0));
        Assert.Equal(payload, actual);
        Assert.False(degraded.CanWrite);
    }

    [Fact]
    public async Task Degraded_write_with_one_missing_member_remains_reopenable()
    {
        MemoryStream[] members = CreateEmptyMembers();
        var options = new ErasureCodeStreamOptions(leaveOpen: true);
        await using (ErasureCodeStream created = await ErasureCodeStream.CreateAsync(
            members,
            DataShardCount,
            ParityShardCount,
            Capacity,
            ShardSize,
            options))
        {
        }

        Stream[] surviving = [members[1], members[2], members[3], members[4]];
        byte[] payload = CreatePayload(20_000);
        await using (ErasureCodeStream degraded = await ErasureCodeStream.OpenAsync(surviving, options))
        {
            Assert.True(degraded.CanWrite);
            await degraded.WriteAtAsync(payload, 1024);
        }

        await using ErasureCodeStream reopened = await ErasureCodeStream.OpenAsync(surviving, options);
        var actual = new byte[payload.Length];
        await reopened.ReadAtAsync(actual, 1024);
        Assert.Equal(payload, actual);
    }

    [Fact]
    public void Create_rejects_capacity_that_is_not_a_complete_stripe()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ErasureCodeStream.Create(
            CreateEmptyMembers(),
            DataShardCount,
            ParityShardCount,
            Capacity - 1,
            ShardSize,
            new ErasureCodeStreamOptions(leaveOpen: true)));
    }

    [Fact]
    public async Task Position_and_explicit_offset_operations_obey_fixed_capacity()
    {
        MemoryStream[] members = CreateEmptyMembers();
        await using ErasureCodeStream stream = await ErasureCodeStream.CreateAsync(
            members,
            DataShardCount,
            ParityShardCount,
            Capacity,
            ShardSize,
            new ErasureCodeStreamOptions(leaveOpen: true));
        stream.Position = 100;
        await stream.WriteAsync(new byte[] { 1, 2, 3 });
        Assert.Equal(103, stream.Position);
        await stream.WriteAtAsync(new byte[] { 4, 5 }, 10);
        Assert.Equal(103, stream.Position);
        Assert.Equal(0, await stream.ReadAtAsync(new byte[10], Capacity));
        Assert.Throws<IOException>(() => stream.Seek(1, SeekOrigin.End));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(Capacity / 2));
    }

    [Fact]
    public async Task State_exposes_member_performance_and_degraded_reconstruction()
    {
        MemoryStream[] members = CreateEmptyMembers();
        var options = new ErasureCodeStreamOptions(leaveOpen: true, latencySampleRate: 1);
        byte[] payload = CreatePayload(ShardSize);
        await using (ErasureCodeStream created = await ErasureCodeStream.CreateAsync(
            members,
            DataShardCount,
            ParityShardCount,
            Capacity,
            ShardSize,
            options))
        {
            await created.WriteAtAsync(payload, 0);
            ErasureCodeStreamState state = created.GetState();
            Assert.Equal(ErasureCodeStreamStatus.Healthy, state.Status);
            Assert.Equal(DataShardCount + ParityShardCount, state.Members.Count);
            Assert.All(state.Members, static member => Assert.True(member.Performance.WriteOperations > 0));
            Assert.All(state.Members, static member => Assert.True(member.Performance.SampledWrites > 0));
        }

        Stream[] surviving = [members[1], members[2], members[3]];
        await using ErasureCodeStream degraded = await ErasureCodeStream.OpenAsync(
            surviving,
            new ErasureCodeStreamOptions(leaveOpen: true, readOnly: true, latencySampleRate: 1));
        var actual = new byte[payload.Length];
        await degraded.ReadAtAsync(actual, 0);

        ErasureCodeStreamState degradedState = degraded.GetState();
        Assert.Equal(ErasureCodeStreamStatus.Degraded, degradedState.Status);
        Assert.Equal(ErasureMemberStatus.Missing, degradedState.Members[0].Status);
        Assert.All(
            degradedState.Members.Where(static member => member.CanRead),
            static member => Assert.True(member.Performance.BytesRead > 0));
    }

    [Fact]
    public async Task Consistency_check_detects_corruption_and_notifies_registered_functions()
    {
        MemoryStream[] members = CreateEmptyMembers();
        var options = new ErasureCodeStreamOptions(leaveOpen: true, latencySampleRate: 1);
        await using ErasureCodeStream stream = await ErasureCodeStream.CreateAsync(
            members,
            DataShardCount,
            ParityShardCount,
            Capacity,
            ShardSize,
            options);
        await stream.WriteAtAsync(CreatePayload(4096), 0);

        ErasureMemberLayout layout = ErasureFormatV1.CalculateLayout(
            ShardSize,
            Capacity / (DataShardCount * ShardSize),
            journalSlotCount: options.JournalSlotCount);
        members[0].GetBuffer()[layout.DataOffset + ErasureFormatV1.ShardHeaderSize + 123] ^= 0x5A;

        var completion = new TaskCompletionSource<ErasureMaintenanceProgress>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable maintenanceRegistration = stream.RegisterMaintenanceHandler(progress =>
        {
            if (progress.Status == ErasureMaintenanceStatus.Completed)
            {
                completion.TrySetResult(progress);
            }
        });
        var transition = new TaskCompletionSource<ErasureCodeStreamStateChangedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable stateRegistration = stream.RegisterStateChangeHandler(args =>
        {
            if (args.Current.Members[0].Status == ErasureMemberStatus.Corrupt)
            {
                transition.TrySetResult(args);
            }
        }, invokeImmediately: false);

        ErasureConsistencyCheckResult result = await stream.CheckConsistencyAsync(
            new ErasureMaintenanceOptions(ErasureMaintenancePriority.Foreground));
        ErasureMaintenanceProgress completed = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ErasureCodeStreamStateChangedEventArgs changed = await transition.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.IsConsistent);
        Assert.Contains(0, result.InconsistentMemberPositions);
        Assert.Equal(result.OperationId, completed.OperationId);
        Assert.Equal(ErasureCodeStreamStatus.Degraded, changed.Current.Status);
        Assert.Equal(ErasureMemberStatus.Corrupt, stream.GetState().Members[0].Status);
    }

    [Fact]
    public async Task Read_reconstructs_a_checksum_corrupted_data_block()
    {
        MemoryStream[] members = CreateEmptyMembers();
        var options = new ErasureCodeStreamOptions(leaveOpen: true);
        byte[] payload = CreatePayload(32_000);
        await using ErasureCodeStream stream = await ErasureCodeStream.CreateAsync(
            members,
            DataShardCount,
            ParityShardCount,
            Capacity,
            ShardSize,
            options);
        await stream.WriteAtAsync(payload, 0);

        ErasureMemberLayout layout = ErasureFormatV1.CalculateLayout(
            ShardSize,
            Capacity / (DataShardCount * ShardSize),
            journalSlotCount: options.JournalSlotCount);
        members[0].GetBuffer()[layout.DataOffset + ErasureFormatV1.ShardHeaderSize + 17] ^= 0x80;
        var actual = new byte[payload.Length];
        await stream.ReadAtAsync(actual, 0);

        Assert.Equal(payload, actual);
        Assert.Equal(ErasureMemberStatus.Corrupt, stream.GetState().Members[0].Status);
    }

    [Fact]
    public async Task Committed_write_is_replayed_after_home_write_failures()
    {
        FaultingMemberStream[] members = Enumerable.Range(0, DataShardCount + ParityShardCount)
            .Select(static _ => new FaultingMemberStream())
            .ToArray();
        var options = new ErasureCodeStreamOptions(leaveOpen: true);
        byte[] payload = CreatePayload(DataShardCount * ShardSize);
        await using (ErasureCodeStream stream = await ErasureCodeStream.CreateAsync(
            members,
            DataShardCount,
            ParityShardCount,
            Capacity,
            ShardSize,
            options))
        {
            foreach (FaultingMemberStream member in members.Take(3))
            {
                member.FailWritesAfter(4);
            }

            await Assert.ThrowsAnyAsync<IOException>(() => stream.WriteAtAsync(payload, 0).AsTask());
            foreach (FaultingMemberStream member in members)
            {
                member.StopFailing();
            }
        }

        await using ErasureCodeStream recovered = await ErasureCodeStream.OpenAsync(members, options);
        var actual = new byte[payload.Length];
        await recovered.ReadAtAsync(actual, 0);
        Assert.Equal(payload, actual);
    }

    [Fact]
    public async Task Checkpointed_set_opens_on_physically_read_only_members()
    {
        MemoryStream[] members = CreateEmptyMembers();
        var options = new ErasureCodeStreamOptions(leaveOpen: true);
        byte[] payload = CreatePayload(24_000);
        await using (ErasureCodeStream created = await ErasureCodeStream.CreateAsync(
            members,
            DataShardCount,
            ParityShardCount,
            Capacity,
            ShardSize,
            options))
        {
            await created.WriteAtAsync(payload, 123);
        }

        Stream[] readOnlyMembers = members
            .Select(static member => (Stream)new MemoryStream(member.ToArray(), writable: false))
            .ToArray();
        await using ErasureCodeStream opened = await ErasureCodeStream.OpenAsync(
            readOnlyMembers,
            new ErasureCodeStreamOptions(readOnly: true));
        var actual = new byte[payload.Length];
        await opened.ReadAtAsync(actual, 123);

        Assert.Equal(payload, actual);
        Assert.True(opened.CanRead);
        Assert.False(opened.CanWrite);
    }

    [Fact]
    public async Task Six_plus_four_reconstructs_with_four_missing_members()
    {
        const int data = 6;
        const int parity = 4;
        const long capacity = data * ShardSize;
        MemoryStream[] members = Enumerable.Range(0, data + parity)
            .Select(static _ => new MemoryStream())
            .ToArray();
        var options = new ErasureCodeStreamOptions(leaveOpen: true);
        byte[] payload = CreatePayload((int)capacity);
        await using (ErasureCodeStream created = await ErasureCodeStream.CreateAsync(
            members,
            data,
            parity,
            capacity,
            ShardSize,
            options))
        {
            await created.WriteAtAsync(payload, 0);
        }

        Stream[] surviving = members.Skip(1).Take(data).ToArray();
        await using ErasureCodeStream degraded = await ErasureCodeStream.OpenAsync(
            surviving,
            new ErasureCodeStreamOptions(leaveOpen: true, readOnly: true));
        var actual = new byte[payload.Length];
        await degraded.ReadAtAsync(actual, 0);

        Assert.Equal(payload, actual);
        Assert.Equal(ErasureCodeStreamStatus.Degraded, degraded.GetState().Status);
    }

    private static MemoryStream[] CreateEmptyMembers() =>
        Enumerable.Range(0, DataShardCount + ParityShardCount)
            .Select(static _ => new MemoryStream())
            .ToArray();

    private static byte[] CreatePayload(int length)
    {
        var payload = new byte[length];
        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(index * 31 + 17);
        }

        return payload;
    }

    private sealed class FaultingMemberStream : Stream
    {
        private readonly MemoryStream _inner = new();
        private int _successfulWritesBeforeFailure = -1;
        private int _writes;

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => _inner.CanWrite;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        internal void FailWritesAfter(int successfulWrites)
        {
            _writes = 0;
            _successfulWritesBeforeFailure = successfulWrites;
        }

        internal void StopFailing() => _successfulWritesBeforeFailure = -1;

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowIfArmed();
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ThrowIfArmed();
            _inner.Write(buffer);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ThrowIfArmed();
            return _inner.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private void ThrowIfArmed()
        {
            if (_successfulWritesBeforeFailure >= 0 && _writes++ >= _successfulWritesBeforeFailure)
            {
                throw new IOException("Injected member write failure.");
            }
        }
    }
}
