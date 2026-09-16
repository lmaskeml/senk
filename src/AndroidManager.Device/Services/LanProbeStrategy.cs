using System.Net;
using System.Net.Sockets;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Device.Services;

public sealed class LanProbeStrategy
{
    private readonly ICompanionDiscoveryService _companion;
    private readonly IDeviceIdentityService _identity;
    private readonly ILogger _logger;

    public LanProbeStrategy(
        ICompanionDiscoveryService companion,
        IDeviceIdentityService identity,
        ILogger? logger = null)
    {
        _companion = companion;
        _identity = identity;
        _logger = logger ?? Log.ForContext<LanProbeStrategy>();
    }

    public async Task<CompanionDevice?> ProbeForTargetAsync(
        WirelessWatchTarget target,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(budget);

        var candidates = BuildCandidateList(target.IpAddress);
        _logger.Debug("[LanProbe] {Count} candidates for {Stable}", candidates.Count, target.StableId);

        using var gate = new SemaphoreSlim(10);
        var tasks = candidates.Select(ip => ProbeHostAsync(ip, target, gate, cts.Token)).ToList();

        while (tasks.Count > 0)
        {
            var finished = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(finished);
            var hit = await finished.ConfigureAwait(false);
            if (hit is not null)
                return hit;
        }

        return null;
    }

    private List<string> BuildCandidateList(string lastKnownIp)
    {
        const int maxCandidates = 24;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();

        void Add(string? ip)
        {
            if (list.Count >= maxCandidates)
                return;
            if (string.IsNullOrWhiteSpace(ip) || !seen.Add(ip))
                return;
            if (!IPAddress.TryParse(ip, out var addr)
                || addr.AddressFamily != AddressFamily.InterNetwork)
                return;
            list.Add(ip);
        }

        string? prefix = null;
        if (!string.IsNullOrWhiteSpace(lastKnownIp))
        {
            var parts = lastKnownIp.Split('.');
            if (parts.Length == 4 && int.TryParse(parts[3], out _))
                prefix = $"{parts[0]}.{parts[1]}.{parts[2]}";
        }

        if (!string.IsNullOrWhiteSpace(prefix))
        {
            foreach (var arpIp in ArpTableReader.GetActiveIpv4Hosts(prefix))
                Add(arpIp);
        }

        Add(lastKnownIp);

        if (!string.IsNullOrWhiteSpace(lastKnownIp) && prefix is not null
            && int.TryParse(lastKnownIp.Split('.')[3], out var last))
        {
            for (var delta = -10; delta <= 10; delta++)
            {
                var candidate = last + delta;
                if (candidate is > 0 and < 255 && candidate != last)
                    Add($"{prefix}.{candidate}");
            }
        }
        else
        {
            foreach (var arpIp in ArpTableReader.GetActiveIpv4Hosts())
                Add(arpIp);
        }

        return list;
    }

    private async Task<CompanionDevice?> ProbeHostAsync(
        string ip,
        WirelessWatchTarget target,
        SemaphoreSlim gate,
        CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await IsTcpOpenAsync(ip, CompanionPorts.TcpPairing, 300, ct).ConfigureAwait(false))
                return null;

            foreach (var cached in _companion.GetCachedDevices())
            {
                if (string.Equals(cached.IpAddress, ip, StringComparison.OrdinalIgnoreCase))
                {
                    if (MatchesTarget(cached, target))
                        return cached;
                }
            }

            return new CompanionDevice
            {
                IpAddress = ip,
                TcpPort = CompanionPorts.TcpPairing,
                AdbPort = CompanionPorts.DefaultAdb,
                DeviceName = $"Companion @ {ip}",
                DeviceModel = "LAN",
                DiscoveredAt = DateTime.Now
            };
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool MatchesTarget(CompanionDevice device, WirelessWatchTarget target)
    {
        if (!string.IsNullOrWhiteSpace(target.DeviceId)
            && string.Equals(device.DeviceId, target.DeviceId, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(target.StableId))
        {
            var sid = _identity.ComputeStableId(device.DeviceId, null, device.Manufacturer, device.DeviceModel);
            if (string.Equals(sid, target.StableId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return string.Equals(device.IpAddress, target.IpAddress, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsTcpOpenAsync(string ip, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeoutMs);
            await client.ConnectAsync(IPAddress.Parse(ip), port, linked.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
