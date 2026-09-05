namespace TeeForge.Networking;

/// <summary>Contains a transport-neutral endpoint hint that the application must authenticate and interpret.</summary>
public class MultipathEndpointAdvertisement
{
    internal MultipathEndpointAdvertisement(string scheme, ReadOnlyMemory<byte> data)
    {
        Scheme = scheme;
        Data = data;
    }

    /// <summary>Gets the transport or application scheme.</summary>
    public string Scheme { get; }
    /// <summary>Gets the opaque application payload.</summary>
    public ReadOnlyMemory<byte> Data { get; }
}
