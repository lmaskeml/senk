using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface IWirelessEndpointResolver
{
    /// <summary>
    /// Resolves a fresh ADB endpoint for a previously known device (mDNS → Companion → cache).
    /// </summary>
    Task<WirelessEndpoint?> ResolveAsync(
        WirelessWatchTarget target,
        CancellationToken cancellationToken = default);
}

public interface IConnectionWatchdog : IDisposable
{
    bool IsWatching { get; }
    string? WatchedEndpoint { get; }
    WirelessLinkStatus CurrentStatus { get; }

    /// <summary>Active per-device guardian keys (StableId).</summary>
    IReadOnlyCollection<string> WatchedStableIds { get; }

    event EventHandler? ConnectionLost;
    event EventHandler<string>? ConnectionRestored;
    event EventHandler<WirelessLinkStatus>? StatusChanged;

    void Start(string ip, int port, int intervalSeconds = 10);
    void Start(WirelessWatchTarget target, int intervalSeconds = 3);

    /// <summary>Stops all device guardians.</summary>
    void Stop();

    /// <summary>Stops guardian for a single device without affecting others.</summary>
    void StopDevice(string stableId);

    bool IsWatchingDevice(string stableId);

    /// <summary>
    /// Triggers reconnect for one device (StableId) or the primary watched device.
    /// Uses Companion/mDNS/cache resolvers — never shells getprop on a dead transport.
    /// </summary>
    /// <returns>True if a reconnect cycle was started.</returns>
    bool RequestReconnect(string? stableId = null);

    /// <summary>
    /// Best-effort: open Wireless Debugging / Developer settings on the last known device.
    /// </summary>
    Task<bool> TryOpenWirelessDebugSettingsAsync(CancellationToken cancellationToken = default);

  /// <summary>USB üzerinden kablosuz hata ayıklama kurtarma (root varsa hızlandırılır).</summary>
    Task<bool> TryRecoverViaUsbAsync(CancellationToken cancellationToken = default);

    DevicePresenceRecord? GetPresenceRecord(string? stableId = null);
}

public interface IConnectionPersistService
{
    Task<IReadOnlyList<SavedConnection>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<SavedConnection?> GetLastAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(SavedConnection connection, CancellationToken cancellationToken = default);
    Task RemoveAsync(string ipAddress, CancellationToken cancellationToken = default);
}
