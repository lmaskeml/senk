using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface IDevicePresenceStore
{
    void RecordCompanionHeartbeat(CompanionDevice device, string stableId);
    void RecordEndpoint(string stableId, string ip, int port, string? networkType = null);
    DevicePresenceRecord? Get(string stableId);
    void InvalidateEndpoint(string stableId);
}

public interface IUsbConnectionMonitor
{
    Task<UsbDeviceLink?> FindUsbLinkAsync(
        WirelessWatchTarget target,
        CancellationToken cancellationToken = default);

    Task<bool> IsUsbOnlineForTargetAsync(
        WirelessWatchTarget target,
        CancellationToken cancellationToken = default);
}

public interface IWirelessDiscoveryChain
{
    Task<WirelessEndpoint?> ResolveWiFiAsync(
        WirelessWatchTarget target,
        CancellationToken cancellationToken = default);

    IReadOnlyList<WirelessDiscoveryAttempt> LastAttempts { get; }
}

public interface IWirelessConnectionOrchestrator
{
    Task<WirelessEndpoint?> ResolveAsync(
        WirelessWatchTarget target,
        CancellationToken cancellationToken = default);

    WirelessDiagnosticScenario InferScenario(
        WirelessWatchTarget target,
        DevicePresenceRecord? presence);
}

public interface IAdvancedNetworkDiagnosticsService
{
    Task<IReadOnlyList<WirelessDiscoveryAttempt>> RunManualPortScanAsync(
        string ipAddress,
        int minPort = 37000,
        int maxPort = 47000,
        CancellationToken cancellationToken = default);
}

public interface IRootWirelessDebugService
{
    Task<bool> IsRootAvailableAsync(CancellationToken cancellationToken = default);

    Task<RootWirelessDebugInfo> ProbeAsync(CancellationToken cancellationToken = default);

    Task<int?> GetTlsPortAsync(CancellationToken cancellationToken = default);

    Task<string?> GetWifiIpAsync(CancellationToken cancellationToken = default);

    Task<WdEnableResult> TryEnableWirelessDebugAsync(CancellationToken cancellationToken = default);

    Task<int?> GetDeviceApiLevelAsync(CancellationToken cancellationToken = default);

    /// <summary>Android 10 ve altı için legacy TCP. API 30+ her zaman false.</summary>
    Task<bool> TryEnableLegacyTcpAdbAsync(int port = 5555, CancellationToken cancellationToken = default);
}

public interface IWirelessDebugRecoveryService
{
    Task<bool> TryRecoverViaUsbAsync(
        WirelessWatchTarget target,
        CancellationToken cancellationToken = default);

    Task<bool> ConnectAfterWirelessDebugOpenAsync(
        string usbSerial,
        string wifiIp,
        int port,
        CancellationToken cancellationToken = default);
}

/// <summary>OEM Doze / app freeze — companion UDP servisini whitelist'e alır.</summary>
public interface ICompanionBatteryGuard
{
    Task<bool> TryEnsureCompanionCanRunInBackgroundAsync(
        string? adbSerial = null,
        CancellationToken cancellationToken = default);
}
