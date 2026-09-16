using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;

namespace AndroidManager.Device.Services;

public sealed class DeviceIdentityService : IDeviceIdentityService
{
    public string ComputeStableId(string? deviceId, string? serial, string? manufacturer, string? model)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
            return $"cid:{deviceId.Trim()}";

        if (!string.IsNullOrWhiteSpace(serial))
            return $"serial:{serial.Trim()}";

        var m = (manufacturer ?? "").Trim();
        var mod = (model ?? "").Trim();
        if (m.Length > 0 || mod.Length > 0)
            return $"hw:{m}:{mod}";

        return string.Empty;
    }

    public DeviceIdentity FromWatchTarget(WirelessWatchTarget target) =>
        new()
        {
            StableId = ComputeStableId(target.DeviceId, target.SerialHint, null, target.DeviceModel)
                       ?? (!string.IsNullOrWhiteSpace(target.DeviceId) ? $"cid:{target.DeviceId}" : ""),
            Serial = target.SerialHint,
            Model = target.DeviceModel,
            LastKnownIp = target.IpAddress,
            LastKnownPort = target.Port,
            Transport = target.Transport,
            LastSeen = DateTime.Now
        };

    public DeviceIdentity FromSavedConnection(SavedConnection connection) =>
        new()
        {
            StableId = !string.IsNullOrWhiteSpace(connection.StableDeviceId)
                ? connection.StableDeviceId
                : ComputeStableId(connection.DeviceId, connection.Serial, connection.Manufacturer, connection.DeviceModel),
            Serial = connection.Serial,
            Manufacturer = connection.Manufacturer,
            Model = connection.DeviceModel,
            LastKnownIp = connection.IpAddress,
            LastKnownPort = connection.Port,
            Transport = connection.Transport,
            PairingState = connection.PairingState,
            LastSeen = connection.LastSeen == default ? connection.LastConnected : connection.LastSeen
        };

    public DeviceIdentity FromCompanion(CompanionDevice device) =>
        new()
        {
            StableId = ComputeStableId(device.DeviceId, null, device.Manufacturer, device.DeviceModel),
            Model = device.DeviceModel,
            Manufacturer = device.Manufacturer,
            AndroidVersion = device.AndroidVersion,
            ApiLevel = device.SdkVersion,
            LastKnownIp = device.IpAddress,
            LastKnownPort = device.EffectiveAdbPort,
            LastSeen = device.DiscoveredAt
        };

    public DeviceIdentity FromWirelessDebug(WirelessDebugDevice device) =>
        new()
        {
            StableId = ComputeStableId(device.DeviceId, null, null, device.DisplayName),
            Model = device.DisplayName,
            LastKnownIp = device.IpAddress,
            LastKnownPort = device.ConnectPort ?? 0,
            LastSeen = DateTime.Now
        };

    public bool MatchesTarget(DeviceIdentity identity, WirelessWatchTarget target)
    {
        if (!string.IsNullOrWhiteSpace(target.DeviceId)
            && identity.StableId.Contains(target.DeviceId, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(target.SerialHint)
            && string.Equals(identity.Serial, target.SerialHint, StringComparison.OrdinalIgnoreCase))
            return true;

        var targetStable = ComputeStableId(target.DeviceId, target.SerialHint, null, target.DeviceModel);
        return !string.IsNullOrWhiteSpace(targetStable)
               && string.Equals(identity.StableId, targetStable, StringComparison.OrdinalIgnoreCase);
    }

    public bool MatchesEndpoint(DeviceIdentity identity, WirelessEndpoint endpoint)
    {
        if (!string.IsNullOrWhiteSpace(endpoint.DeviceId)
            && identity.StableId.Contains(endpoint.DeviceId, StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(identity.LastKnownIp, endpoint.IpAddress, StringComparison.OrdinalIgnoreCase);
    }
}
