using System.Text;

namespace TeeForge.Networking;

/// <summary>Represents one message carried by an optional reverse control channel.</summary>
/// <remarks>
/// Use the Get methods to obtain a payload checked against Kind.
/// </remarks>
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

    private Guid PathId { get; }

    private MultipathStreamMode Mode { get; }

    private int DataShardCount { get; }

    private int ParityShardCount { get; }

    private string? EndpointScheme { get; }

    private ReadOnlyMemory<byte> EndpointData { get; }

    /// <summary>Gets the observed path identifier after checking the message kind.</summary>
    /// <exception cref="InvalidOperationException">The message does not report valid frames on a path.</exception>
    public Guid GetPathReceivingValidFrames()
    {
        RequireKind(MultipathControlMessageKind.PathReceivingValidFrames);
        return PathId;
    }

    /// <summary>Gets the typed mode request after checking the message kind.</summary>
    /// <exception cref="InvalidOperationException">The message is not a mode request.</exception>
    public MultipathModeChangeRequest GetModeChangeRequest()
    {
        RequireKind(MultipathControlMessageKind.ModeChangeRequest);
        return new MultipathModeChangeRequest(Mode, DataShardCount, ParityShardCount);
    }

    /// <summary>Gets the typed endpoint advertisement after checking the message kind.</summary>
    /// <exception cref="InvalidOperationException">The message is not an endpoint advertisement.</exception>
    public MultipathEndpointAdvertisement GetEndpointAdvertisement()
    {
        RequireKind(MultipathControlMessageKind.EndpointAdvertisement);
        return new MultipathEndpointAdvertisement(EndpointScheme!, EndpointData);
    }

    /// <summary>Reports observed valid frames on a path without acknowledging delivery or future reliability.</summary>
    public static MultipathControlMessage CreatePathReceivingValidFrames(Guid pathId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(pathId, Guid.Empty);
        return new MultipathControlMessage(
            MultipathControlMessageKind.PathReceivingValidFrames,
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

    private void RequireKind(MultipathControlMessageKind expected)
    {
        if (Kind != expected)
        {
            throw new InvalidOperationException($"The message does not contain a {expected} payload.");
        }
    }
}
