namespace AndroidManager.Core.Models;

public enum PairingState
{
    Unknown = 0,
    NotPaired,
    Paired,
    PairingRequired,
    PairingExpired
}

public enum EndpointDiscoverySource
{
    Unknown = 0,
    Mdns,
    Companion,
    Cached,
    Manual,
    UsbBootstrap,
    Root,
    LanProbe
}

public enum CompanionPresenceStatus
{
    Unknown = 0,
    CompanionOnline,
    CompanionOffline
}

/// <summary>Stable device identity — IP/port are endpoints, not identity.</summary>
public sealed class DeviceIdentity
{
    public string StableId { get; init; } = "";
    public string Serial { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    public string AndroidVersion { get; init; } = "";
    public int ApiLevel { get; init; }
    public string LastKnownIp { get; init; } = "";
    public int LastKnownPort { get; init; }
    public WirelessTransportKind Transport { get; init; } = WirelessTransportKind.Unknown;
    public PairingState PairingState { get; init; } = PairingState.Unknown;
    public DateTime LastSeen { get; init; } = DateTime.Now;

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Model) ? StableId : Model;
}

public sealed class ResolvedWirelessEndpoint
{
    public string DeviceId { get; init; } = "";
    public string StableId { get; init; } = "";
    public string IpAddress { get; init; } = "";
    public int Port { get; init; }
    public EndpointDiscoverySource Source { get; init; } = EndpointDiscoverySource.Unknown;
    public WirelessTransportKind Transport { get; init; } = WirelessTransportKind.Unknown;
    public bool WirelessDebugLikelyEnabled { get; init; } = true;
    public DateTime LastSeen { get; init; } = DateTime.Now;
    public bool IsAvailable { get; init; } = true;

    public string Endpoint => $"{IpAddress}:{Port}";

    public WirelessEndpoint ToWirelessEndpoint() => new()
    {
        IpAddress = IpAddress,
        Port = Port,
        Source = Source.ToString(),
        DeviceId = DeviceId,
        DeviceName = "",
        WirelessDebugLikelyEnabled = WirelessDebugLikelyEnabled,
        Transport = Transport
    };
}
