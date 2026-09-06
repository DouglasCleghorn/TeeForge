using TeeForge.Mirroring;

namespace TeeForge.Quickstart;

internal static class ReplicationExample
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        byte[] payload = "Replicate writes to multiple writable streams."u8.ToArray();
        await using var first = new MemoryStream();
        await using var second = new MemoryStream();

        // ReplicaStream needs only writable destinations; it does not expose reads or seeking.
        await using (var replicas = new ReplicaStream(
            [first, second], new ReplicaStreamOptions(leaveOpen: true)))
        {
            await replicas.WriteAsync(payload, cancellationToken);
            await replicas.FlushAsync(cancellationToken);
        }

        if (!first.CanWrite || !second.CanWrite ||
            !first.ToArray().AsSpan().SequenceEqual(payload) ||
            !second.ToArray().AsSpan().SequenceEqual(payload))
        {
            throw new InvalidOperationException("Both replicas must receive the payload and remain open.");
        }
    }
}
