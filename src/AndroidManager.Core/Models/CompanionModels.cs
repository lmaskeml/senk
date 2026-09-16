namespace AndroidManager.Core.Models;

public sealed class CompanionDevice
{
    public string DeviceId { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public int AdbPort { get; init; } = 5555;
    /// <summary>Android 11+ TLS wireless debug connect port when readable from Companion.</summary>
    public int? WirelessAdbPort { get; init; }
    /// <summary>Temporary pairing port while “pair with code” UI is open on device.</summary>
    public int? WirelessPairingPort { get; init; }
    public int TcpPort { get; init; } = 37021;
    public string DeviceName { get; init; } = string.Empty;
    public string DeviceModel { get; init; } = string.Empty;
    public string Manufacturer { get; init; } = string.Empty;
    public string AndroidVersion { get; init; } = string.Empty;
    public int SdkVersion { get; init; }
    public string CompanionVersion { get; init; } = string.Empty;
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();
    public bool WirelessDebugEnabled { get; init; }
    public string PortSource { get; init; } = "";
    public string NetworkType { get; init; } = "";
    public int NetworkGeneration { get; init; }
    /// <summary>True when TCP probe found an open ADB connect port.</summary>
    public bool IsAdbReachable { get; init; }
    public DateTime DiscoveredAt { get; init; } = DateTime.Now;

    public int EffectiveAdbPort =>
        WirelessAdbPort is > 0 ? WirelessAdbPort.Value : AdbPort;
}

public sealed class CompanionConnectResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;
    public string DeviceId { get; init; } = string.Empty;
    public string Serial { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public int Port { get; init; }
    public WirelessTransportKind Transport { get; init; } = WirelessTransportKind.Unknown;
}
