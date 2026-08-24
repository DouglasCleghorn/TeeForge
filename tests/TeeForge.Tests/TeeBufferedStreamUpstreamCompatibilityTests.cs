// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Adapted for TeeForge from the BufferedStream tests at dotnet/runtime commit
// 4271d88e0aebf3d04f188f1334c2220d80555ef6.

namespace TeeForge.Tests;

public class TeeBufferedStreamUpstreamCompatibilityTests
{
    [Fact]
    public void Buffer_size_and_underlying_tee_are_exposed()
    {
        using var first = new MemoryStream();
        using var second = new MemoryStream();
        using var stream = new TeeBufferedStream(
            [first, second],
            new TeeBufferedStreamOptions(leaveOpen: true, bufferSize: 1234));

        Assert.Equal(1234, stream.BufferSize);
        Assert.IsType<TeeStream>(stream.UnderlyingStream, exactMatch: false);
    }

    [Fact]
    public void Disposed_destination_is_rejected_without_taking_ownership()
    {
        using var open = new MemoryStream();
        var disposed = new MemoryStream();
        disposed.Dispose();

        Assert.Throws<ObjectDisposedException>(() => new TeeBufferedStream(open, disposed));
        Assert.True(open.CanWrite);
    }

    [Fact]
    public void Seek_within_read_buffer_preserves_mirror_positions()
    {
        byte[] data = Enumerable.Range(0, 16).Select(static value => (byte)value).ToArray();
        using var first = new MemoryStream(data);
        using var second = new MemoryStream(data);
        using var stream = new TeeBufferedStream(
            [first, second],
            new TeeBufferedStreamOptions(leaveOpen: true, bufferSize: 8));

        Span<byte> initial = stackalloc byte[3];
        Assert.Equal(3, stream.Read(initial));
        Assert.Equal([0, 1, 2], initial.ToArray());

        Assert.Equal(1, stream.Seek(1, SeekOrigin.Begin));

        Span<byte> reread = stackalloc byte[4];
        Assert.Equal(4, stream.Read(reread));
        Assert.Equal([1, 2, 3, 4], reread.ToArray());
        Assert.Equal(5, stream.Position);
        Assert.Equal(first.Position, second.Position);
    }

    [Fact]
    public void Read_mismatch_is_reported_when_the_shared_buffer_is_filled()
    {
        using var first = new MemoryStream([1, 2, 3, 4]);
        using var second = new MemoryStream([1, 9, 3, 4]);
        using var stream = new TeeBufferedStream(
            [first, second],
            new TeeBufferedStreamOptions(leaveOpen: true, bufferSize: 4));

        TeeStreamConsistencyException exception =
            Assert.Throws<TeeStreamConsistencyException>(() => stream.ReadByte());

        Assert.Equal("Read", exception.OperationName);
        TeeStreamMismatch mismatch = Assert.Single(exception.Mismatches);
        Assert.Equal(1, mismatch.DestinationIndex);
        Assert.Equal(1, mismatch.FirstDifferingByteOffset);
    }

    [Fact]
    public void Deferred_write_failure_still_attempts_every_destination()
    {
        using var first = new FailFirstWriteStream();
        using var second = new FailFirstWriteStream();
        using var successful = new MemoryStream();
        var stream = new TeeBufferedStream(
            [first, second, successful],
            new TeeBufferedStreamOptions(leaveOpen: true, bufferSize: 16));

        stream.Write([1, 2, 3]);
        AggregateException exception = Assert.Throws<AggregateException>(() => stream.Flush());

        Assert.Equal(2, exception.InnerExceptions.Count);
        Assert.Equal(1, first.WriteAttempts);
        Assert.Equal(1, second.WriteAttempts);
        Assert.Equal([1, 2, 3], successful.ToArray());

        // The first flush did not commit the shared buffer, so disposal retries it.
        stream.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Copy_to_flushes_pending_writes_before_reading(bool asynchronously)
    {
        using var first = new MemoryStream();
        using var second = new MemoryStream();
        using var destination = new MemoryStream();
        using var stream = new TeeBufferedStream(
            [first, second],
            new TeeBufferedStreamOptions(leaveOpen: true, bufferSize: 8));

        stream.Write([1, 2, 3]);
        stream.Position = 0;

        if (asynchronously)
        {
            await stream.CopyToAsync(destination);
        }
        else
        {
            stream.CopyTo(destination);
        }

        Assert.Equal([1, 2, 3], destination.ToArray());
        Assert.Equal(first.ToArray(), second.ToArray());
    }

    [Fact]
    public void Write_after_buffered_read_on_nonseekable_destinations_throws()
    {
        using var first = new NonSeekableReadWriteStream([1, 2, 3, 4]);
        using var second = new NonSeekableReadWriteStream([1, 2, 3, 4]);
        using var stream = new TeeBufferedStream(
            [first, second],
            new TeeBufferedStreamOptions(leaveOpen: true, bufferSize: 4));

        Assert.Equal(1, stream.ReadByte());
        Assert.Throws<NotSupportedException>(() => stream.WriteByte(5));
    }

    private sealed class FailFirstWriteStream : MemoryStream
    {
        public int WriteAttempts { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAttempts++;
            if (WriteAttempts == 1)
            {
                throw new IOException("Expected test failure.");
            }

            base.Write(buffer, offset, count);
        }
    }

    private sealed class NonSeekableReadWriteStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
