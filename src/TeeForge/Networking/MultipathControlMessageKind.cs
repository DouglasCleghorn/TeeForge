namespace TeeForge.Networking;

/// <summary>Identifies an optional reverse-control message.</summary>
public enum MultipathControlMessageKind
{
    /// <summary>Reports that a data path is receiving valid frames.</summary>
    ReliablePath,

    /// <summary>Requests a distribution-mode change from the authoritative sender.</summary>
    ModeChangeRequest,

    /// <summary>Suggests a transport-neutral endpoint to the peer.</summary>
    EndpointAdvertisement,
}
