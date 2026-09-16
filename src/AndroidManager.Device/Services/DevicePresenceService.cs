using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;

namespace AndroidManager.Device.Services;

public sealed class DevicePresenceService : IDevicePresenceService
{
    private readonly ICompanionDiscoveryService _companion;
    private readonly IDeviceIdentityService _identity;

    public DevicePresenceService(
        ICompanionDiscoveryService companion,
        IDeviceIdentityService identity)
    {
        _companion = companion;
        _identity = identity;
    }

    public CompanionPresenceStatus GetCompanionPresence(string? deviceId, string? stableId = null)
    {
        var devices = _companion.GetCachedDevices();
        if (devices.Count == 0)
            return CompanionPresenceStatus.CompanionOffline;

        if (TryFind(devices, deviceId, stableId) is not null)
            return CompanionPresenceStatus.CompanionOnline;

        return CompanionPresenceStatus.CompanionOffline;
    }

    public bool IsCompanionOnline(string? deviceId, string? stableId = null) =>
        GetCompanionPresence(deviceId, stableId) == CompanionPresenceStatus.CompanionOnline;

    private CompanionDevice? TryFind(
        IReadOnlyList<CompanionDevice> devices,
        string? deviceId,
        string? stableId)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var byId = devices.FirstOrDefault(d =>
                string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null) return byId;
        }

        if (!string.IsNullOrWhiteSpace(stableId))
        {
            foreach (var d in devices)
            {
                var sid = _identity.ComputeStableId(d.DeviceId, null, d.Manufacturer, d.DeviceModel);
                if (string.Equals(sid, stableId, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
        }

        return null;
    }
}
