namespace AndroidManager.Core.Models;

public sealed class SavedConnection
{
    public string DeviceId { get; set; } = string.Empty;
    public string Serial { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; } = 5555;
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceModel { get; set; } = string.Empty;
    public DateTime LastConnected { get; set; }
    public bool AutoReconnect { get; set; } = true;
    public WirelessTransportKind Transport { get; set; } = WirelessTransportKind.Unknown;

    /// <summary>Stable identity (Companion ANDROID_ID or ADB serial).</summary>
    public string StableDeviceId { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public int ApiLevel { get; set; }
    public PairingState PairingState { get; set; } = PairingState.Unknown;
    public EndpointDiscoverySource DiscoverySource { get; set; } = EndpointDiscoverySource.Unknown;
    public DateTime LastSeen { get; set; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(DeviceName) ? IpAddress : DeviceName;

    public string LastConnectedFormatted =>
        LastConnected.ToString("yyyy-MM-dd HH:mm");

    public string Endpoint => $"{IpAddress}:{Port}";
}
