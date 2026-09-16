using AndroidManager.Core;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;

namespace AndroidManager.Device.Services;

public sealed class DevicePresenceStore : IDevicePresenceStore
{
    private readonly IPlatformRepository _platform;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DevicePresenceRecord> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public DevicePresenceStore(IPlatformRepository platform)
    {
        _platform = platform;
    }

    public void RecordCompanionHeartbeat(CompanionDevice device, string stableId)
    {
        if (string.IsNullOrWhiteSpace(stableId))
            return;

        var record = _cache.AddOrUpdate(
            stableId,
            _ => new DevicePresenceRecord
            {
                StableId = stableId,
                LastSeen = DateTime.Now,
                LastKnownIp = device.IpAddress,
                LastKnownPort = device.EffectiveAdbPort,
                LastCompanionOnline = true,
                LastNetworkType = device.NetworkType,
                LastNetworkGeneration = device.NetworkGeneration,
                LastPortSource = device.PortSource
            },
            (_, existing) =>
            {
                existing.LastSeen = DateTime.Now;
                existing.LastKnownIp = device.IpAddress;
                existing.LastKnownPort = device.EffectiveAdbPort;
                existing.LastCompanionOnline = true;
                if (!string.IsNullOrWhiteSpace(device.NetworkType))
                    existing.LastNetworkType = device.NetworkType;
                if (device.NetworkGeneration > 0)
                    existing.LastNetworkGeneration = device.NetworkGeneration;
                if (!string.IsNullOrWhiteSpace(device.PortSource))
                    existing.LastPortSource = device.PortSource;
                return existing;
            });

        PersistFireAndForget(record);
    }

    public void RecordEndpoint(string stableId, string ip, int port, string? networkType = null)
    {
        if (string.IsNullOrWhiteSpace(stableId))
            return;

        var record = _cache.AddOrUpdate(
            stableId,
            _ => new DevicePresenceRecord
            {
                StableId = stableId,
                LastSeen = DateTime.Now,
                LastKnownIp = ip,
                LastKnownPort = port,
                LastNetworkType = networkType
            },
            (_, existing) =>
            {
                existing.LastSeen = DateTime.Now;
                existing.LastKnownIp = ip;
                existing.LastKnownPort = port;
                if (!string.IsNullOrWhiteSpace(networkType))
                    existing.LastNetworkType = networkType;
                return existing;
            });

        PersistFireAndForget(record);
    }

    public DevicePresenceRecord? Get(string stableId)
    {
        if (string.IsNullOrWhiteSpace(stableId))
            return null;

        if (_cache.TryGetValue(stableId, out var cached))
            return cached;

        HydrateFireAndForget(stableId);
        return null;
    }

    public void InvalidateEndpoint(string stableId)
    {
        if (string.IsNullOrWhiteSpace(stableId))
            return;

        if (_cache.TryGetValue(stableId, out var existing))
        {
            existing.LastKnownIp = null;
            existing.LastKnownPort = null;
            existing.LastCompanionOnline = false;
            PersistFireAndForget(existing);
        }

        ObservedTask.Run(_platform.InvalidateDevicePresenceEndpointAsync(stableId));
    }

    private void HydrateFireAndForget(string stableId)
    {
        ObservedTask.Run(Task.Run(async () =>
        {
            try
            {
                var record = await _platform
                    .GetDevicePresenceAsync(stableId)
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
                if (record is not null)
                    _cache.TryAdd(stableId, record);
            }
            catch
            {
                // Presence is best-effort; in-memory cache still serves UI
            }
        }));
    }

    private void PersistFireAndForget(DevicePresenceRecord record)
    {
        ObservedTask.Run(Task.Run(async () =>
        {
            try
            {
                await _platform.UpsertDevicePresenceAsync(record).ConfigureAwait(false);
            }
            catch
            {
                // Presence is best-effort; in-memory cache still serves UI
            }
        }));
    }
}
