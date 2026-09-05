namespace TeeForge.Networking;

/// <summary>Identifies an optional reverse-control message.</summary>
public enum MultipathControlMessageKind
{
    /// <summary>Reports observed valid frames on a path; does not acknowledge delivery or guarantee future reliability.</summary>
    PathReceivingValidFrames = 0,

    /// <summary>Compatibility name for <see cref="PathReceivingValidFrames"/>.</summary>
    ReliablePath = PathReceivingValidFrames,

    /// <summary>Requests a distribution-mode change from the authoritative sender.</summary>
    ModeChangeRequest = 1,

    /// <summary>Suggests a transport-neutral endpoint to the peer.</summary>
    EndpointAdvertisement = 2,
}
