namespace AndroidManager.Core.Models;

public enum WirelessLinkState
{
    Idle,
    Connected,
    Unstable,
    Searching,
    Reconnecting,
    NeedsWirelessDebug,
    Failed,
    /// <summary>Active discovery scan (Guardian 2.0).</summary>
    Discovering,
    /// <summary>Connected but health checks failing intermittently.</summary>
    Degraded,
    /// <summary>Companion online; Wireless ADB endpoint unavailable.</summary>
    AdbUnavailable,
    /// <summary>No Companion/mDNS presence on LAN.</summary>
    DeviceOffline,
    /// <summary>Wireless Debugging pairing must be repeated.</summary>
    PairingRequired
}

public enum WirelessTransportKind
{
    Unknown,
    LegacyTcp,
    WirelessTls
}

public sealed class WirelessWatchTarget
{
    public string IpAddress { get; init; } = "";
    public int Port { get; init; } = 5555;
    public string DeviceId { get; init; } = "";
    public string StableId { get; init; } = "";
    public string SerialHint { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public string DeviceModel { get; init; } = "";
    public WirelessTransportKind Transport { get; init; } = WirelessTransportKind.Unknown;
    public bool AutoReconnect { get; init; } = true;
}

public sealed class WirelessEndpoint
{
    public string IpAddress { get; init; } = "";
    public int Port { get; init; }
    public string Source { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public bool WirelessDebugLikelyEnabled { get; init; } = true;
    public WirelessTransportKind Transport { get; init; } = WirelessTransportKind.Unknown;

    public string Endpoint => $"{IpAddress}:{Port}";
}

public sealed class WirelessLinkStatus
{
    public WirelessLinkState State { get; init; }
    public string Message { get; init; } = "";
    public string? Endpoint { get; init; }
    public string? PreviousEndpoint { get; init; }
    public string? DeviceName { get; init; }
    public string? StableId { get; init; }
    public CompanionPresenceStatus CompanionPresence { get; init; } = CompanionPresenceStatus.Unknown;
    public WirelessDiagnosticScenario Scenario { get; init; } = WirelessDiagnosticScenario.Unknown;
    public DateTime? LastSeen { get; init; }
    public string? LastNetworkType { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;

    public bool EndpointChanged =>
        !string.IsNullOrWhiteSpace(PreviousEndpoint)
        && !string.IsNullOrWhiteSpace(Endpoint)
        && !string.Equals(PreviousEndpoint, Endpoint, StringComparison.OrdinalIgnoreCase);
}
