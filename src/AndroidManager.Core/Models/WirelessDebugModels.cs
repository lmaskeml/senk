namespace AndroidManager.Core.Models;

public enum MdnsServiceKind
{
    LegacyAdb,
    TlsConnect,
    TlsPairing
}

public enum DeviceDiscoverySource
{
    Companion,
    WirelessDebug
}

public sealed class MdnsAdbEndpoint
{
    public string InstanceName { get; init; } = string.Empty;
    public string ServiceType { get; init; } = string.Empty;
    public MdnsServiceKind Kind { get; init; }
    public string IpAddress { get; init; } = string.Empty;
    public int Port { get; init; }
}

/// <summary>
/// Event-style mDNS hit used by IMdnsDiscoveryService (raw multicast).
/// Distinct from <see cref="MdnsAdbEndpoint"/> which comes from <c>adb mdns services</c>.
/// </summary>
public sealed class MdnsDevice
{
    public string ServiceType { get; init; } = string.Empty;
    public string HostName { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public int Port { get; init; }
    public string Serial { get; init; } = string.Empty;
    public DateTime FoundAt { get; init; } = DateTime.Now;

    public bool IsPairingService =>
        ServiceType.Contains("pairing", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Android 11+ Wireless Debugging device discovered via adb mDNS (no Companion APK).
/// </summary>
public sealed class WirelessDebugDevice
{
    public string DeviceId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public int? ConnectPort { get; init; }
    public int? PairingPort { get; init; }
    public bool IsLegacyTcp { get; init; }

    public bool CanConnectDirectly => ConnectPort is > 0;
    public bool HasPairingService => PairingPort is > 0;

    public string SubTitle
    {
        get
        {
            if (HasPairingService && ConnectPort is > 0)
                return $"Kablosuz Debug • {IpAddress}:{ConnectPort} (eşleştirme açık)";
            if (HasPairingService)
                return $"Eşleştirme bekliyor • {IpAddress}:{PairingPort}";
            if (IsLegacyTcp)
                return $"ADB TCP • {IpAddress}:{ConnectPort}";
            return $"Kablosuz Debug • {IpAddress}:{ConnectPort}";
        }
    }
}
