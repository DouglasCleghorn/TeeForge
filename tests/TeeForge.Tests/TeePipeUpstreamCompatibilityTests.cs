// These behavioral tests are adapted from System.IO.Pipelines tests in dotnet/runtime
// commit 4271d88e0aebf3d04f188f1334c2220d80555ef6, particularly PipeResetTests,
// ReadAsyncCancellationTests, UnflushedBytesTests, and PipeReaderWriterFacts.
// The .NET Foundation licenses the upstream tests under the MIT license.

using System.Buffers;
using System.IO.Pipelines;

namespace TeeForge.Tests;

public class TeePipeUpstreamCompatibilityTests
{
    [Fact]
    public void Writer_tracks_unflushed_bytes()
    {
        var pipe = new TeePipe(1);
        PipeWriter writer = pipe.Writer;

        writer.GetSpan(10)[0] = 1;
        writer.Advance(1);

        Assert.True(writer.CanGetUnflushedBytes);
        Assert.Equal(1, writer.UnflushedBytes);
        pipe.Readers[0].Complete();
        writer.Complete();
    }

    [Fact]
    public async Task Canceled_token_cancels_read_without_completing_reader()
    {
        var pipe = new TeePipe(1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await pipe.Readers[0].ReadAsync(cancellation.Token));

        await pipe.Writer.WriteAsync(new byte[] { 1 });
        ReadResult result = await pipe.Readers[0].ReadAsync();
        Assert.Single(result.Buffer.ToArray());
        pipe.Readers[0].AdvanceTo(result.Buffer.End);
        pipe.Readers[0].Complete();
        pipe.Writer.Complete();
    }

    [Fact]
    public async Task ReadAtLeast_waits_for_requested_bytes()
    {
        var pipe = new TeePipe(1);
        ValueTask<ReadResult> pending = pipe.Readers[0].ReadAtLeastAsync(4);

        await pipe.Writer.WriteAsync(new byte[] { 1, 2 });
        Assert.False(pending.IsCompleted);
        await pipe.Writer.WriteAsync(new byte[] { 3, 4 });

        ReadResult result = await pending;
        Assert.Equal(4, result.Buffer.Length);
        pipe.Readers[0].AdvanceTo(result.Buffer.End);
        pipe.Readers[0].Complete();
        pipe.Writer.Complete();
    }
}
