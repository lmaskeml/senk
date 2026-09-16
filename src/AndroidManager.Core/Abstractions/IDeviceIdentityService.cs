using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface IDeviceIdentityService
{
    string ComputeStableId(string? deviceId, string? serial, string? manufacturer, string? model);

    DeviceIdentity FromWatchTarget(WirelessWatchTarget target);

    DeviceIdentity FromSavedConnection(SavedConnection connection);

    DeviceIdentity FromCompanion(CompanionDevice device);

    DeviceIdentity FromWirelessDebug(WirelessDebugDevice device);

    bool MatchesTarget(DeviceIdentity identity, WirelessWatchTarget target);

    bool MatchesEndpoint(DeviceIdentity identity, WirelessEndpoint endpoint);
}

public interface IDevicePresenceService
{
    CompanionPresenceStatus GetCompanionPresence(string? deviceId, string? stableId = null);

    bool IsCompanionOnline(string? deviceId, string? stableId = null);
}

public interface IPairingCredentialStore
{
    Task<PairingState> GetPairingStateAsync(string stableId, CancellationToken cancellationToken = default);
    Task SetPairedAsync(string stableId, CancellationToken cancellationToken = default);
    Task SetPairingRequiredAsync(string stableId, CancellationToken cancellationToken = default);
    Task ClearAsync(string stableId, CancellationToken cancellationToken = default);
}
