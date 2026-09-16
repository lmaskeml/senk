using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

/// <summary>
/// Discovers ADB-over-Wi‑Fi endpoints via <c>adb mdns services</c>
/// (Android 9 <c>_adb._tcp</c> and Android 11+ TLS wireless debugging).
/// </summary>
public interface IWirelessDebugDiscoveryService : IDisposable
{
    event EventHandler<WirelessDebugDevice>? DeviceDiscovered;

    Task StartScanningAsync(CancellationToken cancellationToken = default);
    void StopScanning();

    /// <summary>Also probe these hosts (last connections) for ADB TCP / wireless ports.</summary>
    void HintKnownEndpoints(IReadOnlyList<(string Ip, int Port)> endpoints);

    Task<IReadOnlyList<MdnsAdbEndpoint>> ListMdnsEndpointsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WirelessDebugDevice>> GetDiscoveredDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Pairs (if needed) then connects. For pairing-only advertisements, <paramref name="pairCode"/> is required.
    /// </summary>
    Task<CompanionConnectResult> ConnectAsync(
        WirelessDebugDevice device,
        string? pairCode = null,
        CancellationToken cancellationToken = default);
}
