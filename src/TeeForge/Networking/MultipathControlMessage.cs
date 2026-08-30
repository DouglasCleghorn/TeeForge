using System.Text;

namespace TeeForge.Networking;

/// <summary>Represents one message carried by an optional reverse control channel.</summary>
public class MultipathControlMessage
{
    private MultipathControlMessage(
        MultipathControlMessageKind kind,
        Guid pathId,
        MultipathStreamMode mode,
        int dataShardCount,
        int parityShardCount,
        string? endpointScheme,
        ReadOnlyMemory<byte> endpointData)
    {
        Kind = kind;
        PathId = pathId;
        Mode = mode;
        DataShardCount = dataShardCount;
        ParityShardCount = parityShardCount;
        EndpointScheme = endpointScheme;
        EndpointData = endpointData.ToArray();
    }

    /// <summary>Gets the message kind.</summary>
    public MultipathControlMessageKind Kind { get; }

    /// <summary>Gets the reported path identifier for a reliable-path message.</summary>
    public Guid PathId { get; }

    /// <summary>Gets the requested distribution mode for a mode-change message.</summary>
    public MultipathStreamMode Mode { get; }

    /// <summary>Gets the requested data-shard count for an erasure mode change.</summary>
    public int DataShardCount { get; }

    /// <summary>Gets the requested parity-shard count for an erasure mode change.</summary>
    public int ParityShardCount { get; }

    /// <summary>Gets the endpoint scheme for an endpoint-advertisement message.</summary>
    public string? EndpointScheme { get; }

    /// <summary>Gets the opaque endpoint data for an endpoint-advertisement message.</summary>
    public ReadOnlyMemory<byte> EndpointData { get; }

    /// <summary>Creates a message reporting a path that is receiving valid frames.</summary>
    public static MultipathControlMessage CreateReliablePath(Guid pathId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(pathId, Guid.Empty);
        return new MultipathControlMessage(
            MultipathControlMessageKind.ReliablePath,
            pathId,
            default,
            0,
            0,
            null,
            default);
    }

    /// <summary>Creates a request for a mode change.</summary>
    public static MultipathControlMessage CreateModeChangeRequest(
        MultipathStreamMode mode,
        int dataShardCount = 0,
        int parityShardCount = 0)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (mode == MultipathStreamMode.ErasureCode)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(dataShardCount, 2);
            ArgumentOutOfRangeException.ThrowIfLessThan(parityShardCount, 1);
            if ((long)dataShardCount + parityShardCount > byte.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(parityShardCount));
            }
        }
        else if (dataShardCount != 0 || parityShardCount != 0)
        {
            throw new ArgumentException("Shard counts apply only to erasure mode.");
        }

        return new MultipathControlMessage(
            MultipathControlMessageKind.ModeChangeRequest,
            Guid.Empty,
            mode,
            dataShardCount,
            parityShardCount,
            null,
            default);
    }

    /// <summary>Creates a transport-neutral endpoint advertisement.</summary>
    public static MultipathControlMessage CreateEndpointAdvertisement(
        string endpointScheme,
        ReadOnlyMemory<byte> endpointData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointScheme);
        if (Encoding.UTF8.GetByteCount(endpointScheme) > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(endpointScheme));
        }

        if (endpointData.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(endpointData));
        }

        return new MultipathControlMessage(
            MultipathControlMessageKind.EndpointAdvertisement,
            Guid.Empty,
            default,
            0,
            0,
            endpointScheme,
            endpointData);
    }

    internal static MultipathControlMessage DecodeReliablePath(Guid pathId) =>
        CreateReliablePath(pathId);

    internal static MultipathControlMessage DecodeModeChange(
        MultipathStreamMode mode,
        int dataShardCount,
        int parityShardCount) =>
        CreateModeChangeRequest(mode, dataShardCount, parityShardCount);

    internal static MultipathControlMessage DecodeEndpoint(string scheme, ReadOnlyMemory<byte> data) =>
        CreateEndpointAdvertisement(scheme, data);
}
