using System.Collections.Concurrent;
using TeeForge.Composition;

namespace TeeForge.Tests;

public class MigratingStreamTests
{
    [Fact]
    public async Task Migration_copies_the_complete_sequence_and_preserves_backing_positions()
    {
        byte[] payload = Enumerable.Range(0, 257).Select(static value => (byte)value).ToArray();
        using var source = new MemoryStream(payload);
        using var destination = new MemoryStream(new byte[500], writable: true);
        source.Position = 37;
        destination.Position = 41;
        var options = new MigratingStreamOptions(
            leaveSourceOpen: true,
            leaveDestinationOpen: true,
            bufferSize: 17);
        await using var stream = new MigratingStream(source, destination, options);

        await stream.MigrationCompletion;

        Assert.Equal(payload, destination.ToArray());
        Assert.Equal(37, source.Position);
        Assert.Equal(41, destination.Position);
        Assert.Equal(37, stream.Position);
    }

    [Fact]
    public async Task Reads_use_migrated_destination_and_unmigrated_source_ranges()
    {
        var events = new ConcurrentQueue<string>();
        using var source = new RecordingMemoryStream([0, 1, 2, 3, 4, 5, 6, 7], "source", events);
        using var destination = new GatedFirstWriteStream(
            "destination",
            events,
            gateSecondWrite: true);
        using var cancellation = new CancellationTokenSource();
        var options = new MigratingStreamOptions(
            leaveSourceOpen: true,
            leaveDestinationOpen: true,
            bufferSize: 4);
        await using var stream = new MigratingStream(source, destination, options, cancellation.Token);
        await destination.FirstWriteStarted;

        byte[] migrated = new byte[1];
        byte[] unmigrated = new byte[1];
        ValueTask<int> migratedRead = stream.ReadAtAsync(migrated, 1);
        ValueTask<int> unmigratedRead = stream.ReadAtAsync(unmigrated, 6);
        destination.ReleaseFirstWrite();

        Assert.Equal(1, await migratedRead);
        Assert.Equal(1, await unmigratedRead);
        Assert.Equal(1, migrated[0]);
        Assert.Equal(6, unmigrated[0]);
        await destination.SecondWriteStarted;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await stream.MigrationCompletion);

        string[] recorded = events.ToArray();
        int destinationRead = Array.FindIndex(recorded, static item => item == "destination-read-1-1");
        int sourceRead = Array.FindIndex(recorded, static item => item == "source-read-6-1");
        Assert.True(destinationRead >= 0, string.Join(", ", recorded));
        Assert.True(sourceRead >= 0, string.Join(", ", recorded));
    }

    [Fact]
    public async Task Queued_foreground_operation_precedes_the_next_migration_quantum()
    {
        var events = new ConcurrentQueue<string>();
        using var source = new RecordingMemoryStream([0, 1, 2, 3, 4, 5, 6, 7], "source", events);
        using var destination = new GatedFirstWriteStream("destination", events);
        var options = new MigratingStreamOptions(
            leaveSourceOpen: true,
            leaveDestinationOpen: true,
            bufferSize: 4);
        await using var stream = new MigratingStream(source, destination, options);
        await destination.FirstWriteStarted;

        byte[] value = new byte[1];
        ValueTask<int> foregroundRead = stream.ReadAtAsync(value, 6);
        destination.ReleaseFirstWrite();
        Assert.Equal(1, await foregroundRead);
        await stream.MigrationCompletion;

        string[] recorded = events.ToArray();
        int foreground = Array.FindIndex(recorded, static item => item == "source-read-6-1");
        int secondQuantum = Array.FindIndex(recorded, static item => item == "source-read-4-4");
        Assert.True(foreground >= 0, string.Join(", ", recorded));
        Assert.True(secondQuantum > foreground, string.Join(", ", recorded));
    }

    [Fact]
    public async Task Write_during_migration_is_preserved_in_both_streams()
    {
        using var source = new MemoryStream([0, 1, 2, 3, 4, 5, 6, 7]);
        using var destination = new GatedFirstWriteStream();
        var options = new MigratingStreamOptions(
            leaveSourceOpen: true,
            leaveDestinationOpen: true,
            bufferSize: 4);
        await using var stream = new MigratingStream(source, destination, options);
        await destination.FirstWriteStarted;

        ValueTask write = stream.WriteAtAsync(new byte[] { 40, 50 }, 4);
        destination.ReleaseFirstWrite();
        await write;
        await stream.MigrationCompletion;

        Assert.Equal([0, 1, 2, 3, 40, 50, 6, 7], source.ToArray());
        Assert.Equal(source.ToArray(), destination.ToArray());
    }

    [Fact]
    public async Task Writes_and_length_changes_can_extend_the_live_sequence()
    {
        using var source = new MemoryStream();
        source.Write([1, 2, 3]);
        source.Position = 0;
        using var destination = new GatedFirstWriteStream();
        var options = new MigratingStreamOptions(
            leaveSourceOpen: true,
            leaveDestinationOpen: true,
            bufferSize: 2);
        await using var stream = new MigratingStream(source, destination, options);
        await destination.FirstWriteStarted;

        ValueTask foreground = stream.WriteAtAsync(new byte[] { 0, 7, 8 }, 3);
        destination.ReleaseFirstWrite();
        await foreground;
        await stream.MigrationCompletion;

        Assert.Equal(6, stream.Length);
        Assert.Equal([1, 2, 3, 0, 7, 8], destination.ToArray());
        Assert.Equal(destination.ToArray(), source.ToArray());
    }

    [Fact]
    public async Task Destination_failure_faults_migration_and_source_remains_usable()
    {
        using var source = new MemoryStream([1, 2, 3, 4]);
        using var destination = new ThrowingWriteStream();
        var options = new MigratingStreamOptions(
            leaveSourceOpen: true,
            leaveDestinationOpen: true,
            bufferSize: 2);
        await using var stream = new MigratingStream(source, destination, options);

        IOException failure = await Assert.ThrowsAsync<IOException>(
            async () => await stream.MigrationCompletion);
        Assert.Equal("destination failed", failure.Message);

        stream.WriteAt([9], 1);
        byte[] value = new byte[4];
        Assert.Equal(4, stream.ReadAt(value, 0));
        Assert.Equal([1, 9, 3, 4], value);
        Assert.Equal([1, 9, 3, 4], source.ToArray());
    }

    [Fact]
    public async Task Cancellation_stops_migration_and_source_remains_usable()
    {
        using var source = new MemoryStream([1, 2, 3, 4]);
        using var destination = new GatedFirstWriteStream();
        using var cancellation = new CancellationTokenSource();
        var options = new MigratingStreamOptions(
            leaveSourceOpen: true,
            leaveDestinationOpen: true,
            bufferSize: 2);
        await using var stream = new MigratingStream(source, destination, options, cancellation.Token);
        await destination.FirstWriteStarted;

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await stream.MigrationCompletion);

        stream.WriteAt([8], 0);
        Assert.Equal([8, 2, 3, 4], source.ToArray());
    }

    [Fact]
    public async Task Optional_cleanup_truncates_source_after_destination_is_complete()
    {
        using var source = new MemoryStream([1, 2, 3, 4]);
        using var destination = new MemoryStream();
        var options = new MigratingStreamOptions(
            leaveSourceOpen: true,
            leaveDestinationOpen: true,
            truncateSourceOnCompletion: true,
            bufferSize: 2);
        await using var stream = new MigratingStream(source, destination, options);

        await stream.MigrationCompletion;

        Assert.Empty(source.ToArray());
        Assert.Equal([1, 2, 3, 4], destination.ToArray());
        byte[] data = new byte[4];
        Assert.Equal(4, stream.ReadAt(data, 0));
        Assert.Equal([1, 2, 3, 4], data);
    }

    [Fact]
    public async Task DisposeAsync_honors_independent_ownership_options()
    {
        var source = new TrackingMemoryStream([1, 2, 3]);
        var destination = new TrackingMemoryStream();
        var options = new MigratingStreamOptions(
            leaveSourceOpen: true,
            leaveDestinationOpen: false,
            bufferSize: 2);
        var stream = new MigratingStream(source, destination, options);
        await stream.MigrationCompletion;

        await stream.DisposeAsync();

        Assert.False(source.IsDisposed);
        Assert.True(destination.IsDisposed);
        source.Dispose();
    }

    [Fact]
    public void Constructor_validates_stream_capabilities_and_options()
    {
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentException>(() => new MigratingStream(stream, stream));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MigratingStreamOptions(bufferSize: 0));
        using var readOnly = new MemoryStream(Array.Empty<byte>(), writable: false);
        Assert.Throws<ArgumentException>(() => new MigratingStream(readOnly, stream));
    }

    [Fact]
    public async Task HandoffStream_uses_migration_during_copy_and_destination_after_completion()
    {
        using var source = new MemoryStream([1, 2, 3, 4]);
        using var destination = new GatedFirstWriteStream();
        var options = new MigratingStreamOptions(
            leaveSourceOpen: true,
            leaveDestinationOpen: true,
            bufferSize: 2);
        await using var handoff = new HandoffStream(source, leaveOpen: true);

        Task migration = handoff.MigrateAsync(destination, options);
        await destination.FirstWriteStarted;
        ValueTask foregroundWrite = handoff.WriteAtAsync(new byte[] { 9 }, 1);
        destination.ReleaseFirstWrite();
        await foregroundWrite;
        await migration;

        source.Position = 1;
        source.WriteByte(7);
        byte[] value = new byte[1];
        Assert.Equal(1, handoff.ReadAt(value, 1));
        Assert.Equal(9, value[0]);
        Assert.Equal([1, 9, 3, 4], destination.ToArray());
    }

    [Fact]
    public async Task HandoffStream_restores_source_when_migration_fails()
    {
        using var source = new MemoryStream([1, 2, 3, 4]);
        using var destination = new ThrowingWriteStream();
        var options = new MigratingStreamOptions(
            leaveSourceOpen: true,
            leaveDestinationOpen: true,
            bufferSize: 2);
        await using var handoff = new HandoffStream(source, leaveOpen: true);

        await Assert.ThrowsAsync<IOException>(
            async () => await handoff.MigrateAsync(destination, options));
        await handoff.WriteAtAsync(new byte[] { 8 }, 0);

        Assert.Equal([8, 2, 3, 4], source.ToArray());
    }

    [Fact]
    public async Task HandoffStream_transfers_destination_ownership_after_success()
    {
        var source = new TrackingMemoryStream([1, 2, 3]);
        var destination = new TrackingMemoryStream();
        var options = new MigratingStreamOptions(
            leaveSourceOpen: false,
            leaveDestinationOpen: false,
            bufferSize: 2);
        var handoff = new HandoffStream(source, leaveOpen: true);

        await handoff.MigrateAsync(destination, options);

        Assert.True(source.IsDisposed);
        Assert.False(destination.IsDisposed);
        byte[] data = new byte[3];
        Assert.Equal(3, handoff.ReadAt(data, 0));
        Assert.Equal([1, 2, 3], data);

        await handoff.DisposeAsync();
        Assert.False(destination.IsDisposed);
        destination.Dispose();
    }

    private class RecordingMemoryStream : MemoryStream
    {
        private readonly string _name;
        private readonly ConcurrentQueue<string> _events;

        public RecordingMemoryStream(
            byte[] initial,
            string name,
            ConcurrentQueue<string> events)
            : base(initial.Length + 16)
        {
            Write(initial);
            Position = 0;
            SetLength(initial.Length);
            _name = name;
            _events = events;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _events.Enqueue($"{_name}-read-{Position}-{buffer.Length}");
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class GatedFirstWriteStream : RecordingMemoryStream
    {
        private readonly TaskCompletionSource _firstWriteStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstWrite = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondWriteStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSecondWrite = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _gateSecondWrite;
        private int _writes;

        public GatedFirstWriteStream()
            : this("destination", new ConcurrentQueue<string>())
        {
        }

        public GatedFirstWriteStream(
            string name,
            ConcurrentQueue<string> events,
            bool gateSecondWrite = false)
            : base([], name, events)
        {
            _gateSecondWrite = gateSecondWrite;
        }

        public Task FirstWriteStarted => _firstWriteStarted.Task;

        public Task SecondWriteStarted => _secondWriteStarted.Task;

        public void ReleaseFirstWrite() => _releaseFirstWrite.TrySetResult();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int writeNumber = Interlocked.Increment(ref _writes);
            if (writeNumber == 1)
            {
                _firstWriteStarted.TrySetResult();
                await _releaseFirstWrite.Task.WaitAsync(cancellationToken);
            }
            else if (_gateSecondWrite && writeNumber == 2)
            {
                _secondWriteStarted.TrySetResult();
                await _releaseSecondWrite.Task.WaitAsync(cancellationToken);
            }

            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class ThrowingWriteStream : MemoryStream
    {
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("destination failed"));
    }

    private sealed class TrackingMemoryStream : MemoryStream
    {
        public TrackingMemoryStream()
        {
        }

        public TrackingMemoryStream(byte[] initial)
            : base(initial)
        {
        }

        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
