namespace AndroidManager.Core.Models;

public enum WirelessDiagnosticScenario
{
    Unknown = 0,
    PortChanged,
    WirelessDebugOff,
    PortUnreadable,
    PairingRequired,
    NetworkChanged,
    DeviceOnCellular,
    DeviceOffline,
    RootAssisted
}

public enum WirelessDiscoveryStep
{
    RootProbe,
    Mdns,
    CompanionUdp,
    LanProbe,
    CachedEndpoint,
    LegacyTcp,
    PortScanManual
}

public sealed class WirelessDiscoveryAttempt
{
    public WirelessDiscoveryStep Step { get; init; }
    public bool Success { get; init; }
    public TimeSpan Duration { get; init; }
    public string? Detail { get; init; }
}

public enum WdEnableResult
{
    NotSupported,
    MaybeEnabled,
    Failed,
    PortAlreadyOpen
}

public sealed class UsbDeviceLink
{
    public string Serial { get; init; } = "";
    public string StableId { get; init; } = "";
    public bool IsOnline { get; init; }
}

public sealed class RootWirelessDebugInfo
{
    public bool RootAvailable { get; init; }
    public bool WirelessDebugEnabled { get; init; }
    public int? TlsPort { get; init; }
    public string? WifiIp { get; init; }
    public bool LegacyTcpEnabled { get; init; }
}
