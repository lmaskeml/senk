using System.Diagnostics;
using System.Net.Sockets;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Device.Services;

public sealed class WirelessDiscoveryChain : IWirelessDiscoveryChain
{
    private const int Android11ApiLevel = 30;

    private readonly IWirelessDebugDiscoveryService _wireless;
    private readonly ICompanionDiscoveryService _companion;
    private readonly LanProbeStrategy _lanProbe;
    private readonly IRootWirelessDebugService _rootWireless;
    private readonly IDeviceIdentityService _identity;
    private readonly ISettingsService _settings;
    private readonly IDevicePresenceStore _presenceStore;
    private readonly ILogger _logger;
    private readonly List<WirelessDiscoveryAttempt> _lastAttempts = [];

    public WirelessDiscoveryChain(
        IWirelessDebugDiscoveryService wireless,
        ICompanionDiscoveryService companion,
        LanProbeStrategy lanProbe,
        IRootWirelessDebugService rootWireless,
        IDeviceIdentityService identity,
        ISettingsService settings,
        IDevicePresenceStore presenceStore,
        ILogger? logger = null)
    {
        _wireless = wireless;
        _companion = companion;
        _lanProbe = lanProbe;
        _rootWireless = rootWireless;
        _identity = identity;
        _settings = settings;
        _presenceStore = presenceStore;
        _logger = logger ?? Log.ForContext<WirelessDiscoveryChain>();
    }

    public IReadOnlyList<WirelessDiscoveryAttempt> LastAttempts => _lastAttempts;

    public async Task<WirelessEndpoint?> ResolveWiFiAsync(
        WirelessWatchTarget target,
        CancellationToken cancellationToken = default)
    {
        _lastAttempts.Clear();
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(TimeSpan.FromSeconds(10));

        var ct = budgetCts.Token;

        var rootEp = await TryRootAsync(target, ct).ConfigureAwait(false);
        if (rootEp is not null)
            return rootEp;

        var phase1Runners = new List<Func<CancellationToken, Task<WirelessEndpoint?>>>();
        if (_settings.Current.WirelessMdnsDiscovery)
            phase1Runners.Add(c => TryMdnsAsync(target, TimeSpan.FromSeconds(3), c));
        if (_settings.Current.WirelessCompanionDiscovery)
            phase1Runners.Add(c => TryCompanionAsync(target, TimeSpan.FromSeconds(3), c));

        if (phase1Runners.Count > 0)
        {
            var fast = await RaceUntilSuccessAsync(
                TimeSpan.FromSeconds(3),
                ct,
                phase1Runners).ConfigureAwait(false);
            if (fast is not null)
                return fast;
        }

        var phase2 = await RaceUntilSuccessAsync(
            TimeSpan.FromSeconds(5),
            ct,
            [
                c => TryLanProbeAsync(target, TimeSpan.FromSeconds(5), c),
                c => TryCachedAsync(target, TimeSpan.FromSeconds(5), c)
            ]).ConfigureAwait(false);
        if (phase2 is not null)
            return phase2;

        return await TimedStepAsync(
            WirelessDiscoveryStep.LegacyTcp,
            () => TryLegacyAsync(target, TimeSpan.FromSeconds(2), ct),
            ct).ConfigureAwait(false);
    }

    private async Task<WirelessEndpoint?> RaceUntilSuccessAsync(
        TimeSpan budget,
        CancellationToken ct,
        IReadOnlyList<Func<CancellationToken, Task<WirelessEndpoint?>>> runners)
    {
        using var phaseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        phaseCts.CancelAfter(budget);

        var pending = runners.Select(r => r(phaseCts.Token)).ToList();
        while (pending.Count > 0 && !phaseCts.Token.IsCancellationRequested)
        {
            var finished = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(finished);

            try
            {
                var ep = await finished.ConfigureAwait(false);
                if (ep is not null)
                    return ep;
            }
            catch (OperationCanceledException) when (phaseCts.Token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Debug(ex, "[Discovery] Parallel runner failed");
            }
        }

        return null;
    }

    private async Task<WirelessEndpoint?> TimedStepAsync(
        WirelessDiscoveryStep step,
        Func<Task<WirelessEndpoint?>> action,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        WirelessEndpoint? result = null;
        string? detail = null;
        try
        {
            result = await action().ConfigureAwait(false);
            detail = result?.Endpoint;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            detail = ex.Message;
        }

        sw.Stop();
        _lastAttempts.Add(new WirelessDiscoveryAttempt
        {
            Step = step,
            Success = result is not null,
            Duration = sw.Elapsed,
            Detail = detail
        });

        if (result is not null)
            _logger.Information(
                "[Discovery] Step={Step} OK Endpoint={Ep} in {Ms}ms",
                step, result.Endpoint, sw.ElapsedMilliseconds);

        return result;
    }

    private async Task<WirelessEndpoint?> TryRootAsync(
        WirelessWatchTarget target,
        CancellationToken ct)
    {
        using var rootCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        rootCts.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            if (!await _rootWireless.IsRootAvailableAsync(rootCts.Token).ConfigureAwait(false))
                return null;

            var info = await _rootWireless.ProbeAsync(rootCts.Token).ConfigureAwait(false);
            if (info.TlsPort is not > 0)
                return null;

            var ip = info.WifiIp ?? target.IpAddress;
            if (string.IsNullOrWhiteSpace(ip))
                return null;

            var ep = new WirelessEndpoint
            {
                IpAddress = ip,
                Port = info.TlsPort!.Value,
                Source = EndpointDiscoverySource.Root.ToString(),
                DeviceId = target.DeviceId,
                DeviceName = target.DeviceName,
                Transport = WirelessTransportKind.WirelessTls,
                WirelessDebugLikelyEnabled = true
            };

            if (!await IsOpenAsync(ep.IpAddress, ep.Port, 400, rootCts.Token).ConfigureAwait(false))
                return null;

            _lastAttempts.Add(new WirelessDiscoveryAttempt
            {
                Step = WirelessDiscoveryStep.RootProbe,
                Success = true,
                Duration = TimeSpan.FromMilliseconds(500),
                Detail = ep.Endpoint
            });
            return ep;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<WirelessEndpoint?> TryMdnsAsync(
        WirelessWatchTarget target,
        TimeSpan budget,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget);

        var devices = await _wireless.GetDiscoveredDevicesAsync(cts.Token).ConfigureAwait(false);
        var match = MatchWireless(devices, target);
        if (match?.ConnectPort is not > 0)
            return null;

        var ep = new WirelessEndpoint
        {
            IpAddress = match.IpAddress,
            Port = match.ConnectPort!.Value,
            Source = EndpointDiscoverySource.Mdns.ToString(),
            DeviceId = match.DeviceId,
            DeviceName = match.DisplayName,
            Transport = match.IsLegacyTcp ? WirelessTransportKind.LegacyTcp : WirelessTransportKind.WirelessTls,
            WirelessDebugLikelyEnabled = true
        };

        return await IsOpenAsync(ep.IpAddress, ep.Port, 350, cts.Token).ConfigureAwait(false)
            ? ep
            : null;
    }

    private async Task<WirelessEndpoint?> TryCompanionAsync(
        WirelessWatchTarget target,
        TimeSpan budget,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget);

        var devices = _companion.GetCachedDevices();
        var match = MatchCompanion(devices, target);
        if (match is null)
            return null;

        var port = match.EffectiveAdbPort;
        if (port <= 0 && !match.WirelessDebugEnabled)
            return new WirelessEndpoint
            {
                IpAddress = match.IpAddress,
                Port = target.Port > 0 ? target.Port : match.AdbPort,
                Source = EndpointDiscoverySource.Companion.ToString(),
                DeviceId = match.DeviceId,
                DeviceName = match.DeviceName,
                Transport = WirelessTransportKind.WirelessTls,
                WirelessDebugLikelyEnabled = false
            };

        if (port <= 0)
            return null;

        var ep = new WirelessEndpoint
        {
            IpAddress = match.IpAddress,
            Port = port,
            Source = EndpointDiscoverySource.Companion.ToString(),
            DeviceId = match.DeviceId,
            DeviceName = match.DeviceName,
            Transport = match.WirelessAdbPort is > 0
                ? WirelessTransportKind.WirelessTls
                : WirelessTransportKind.LegacyTcp,
            WirelessDebugLikelyEnabled = match.WirelessDebugEnabled || match.WirelessAdbPort is > 0
        };

        return await IsOpenAsync(ep.IpAddress, ep.Port, 350, cts.Token).ConfigureAwait(false)
            ? ep
            : ep.WirelessDebugLikelyEnabled ? ep : null;
    }

    private async Task<WirelessEndpoint?> TryLanProbeAsync(
        WirelessWatchTarget target,
        TimeSpan budget,
        CancellationToken ct)
    {
        var hit = await _lanProbe.ProbeForTargetAsync(target, budget, ct).ConfigureAwait(false);
        if (hit is null)
            return null;

        var port = hit.EffectiveAdbPort;
        if (port <= 0)
            return null;

        var ep = new WirelessEndpoint
        {
            IpAddress = hit.IpAddress,
            Port = port,
            Source = EndpointDiscoverySource.LanProbe.ToString(),
            DeviceId = hit.DeviceId,
            DeviceName = hit.DeviceName,
            Transport = hit.WirelessAdbPort is > 0
                ? WirelessTransportKind.WirelessTls
                : WirelessTransportKind.LegacyTcp,
            WirelessDebugLikelyEnabled = hit.WirelessDebugEnabled || hit.WirelessAdbPort is > 0
        };

        return await IsOpenAsync(ep.IpAddress, ep.Port, 350, ct).ConfigureAwait(false) ? ep : null;
    }

    private async Task<WirelessEndpoint?> TryCachedAsync(
        WirelessWatchTarget target,
        TimeSpan budget,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget);

        if (string.IsNullOrWhiteSpace(target.IpAddress) || target.Port is < 1 or > 65535)
            return null;

        if (!await EndpointProbeHelper.IsTcpOpenAsync(
                target.IpAddress,
                target.Port,
                EndpointProbeHelper.StaleEndpointTimeoutMs,
                cts.Token).ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(target.StableId))
            {
                _presenceStore.InvalidateEndpoint(target.StableId);
                _logger.Warning(
                    "[Discovery] Stale cached endpoint {Ip}:{Port} for {Stable} — invalidated",
                    target.IpAddress,
                    target.Port,
                    target.StableId);
            }

            return null;
        }

        return new WirelessEndpoint
        {
            IpAddress = target.IpAddress,
            Port = target.Port,
            Source = EndpointDiscoverySource.Cached.ToString(),
            DeviceId = target.DeviceId,
            DeviceName = target.DeviceName,
            Transport = target.Transport,
            WirelessDebugLikelyEnabled = true
        };
    }

    private async Task<WirelessEndpoint?> TryLegacyAsync(
        WirelessWatchTarget target,
        TimeSpan budget,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget);

        if (string.IsNullOrWhiteSpace(target.IpAddress))
            return null;

        var api = await _rootWireless.GetDeviceApiLevelAsync(cts.Token).ConfigureAwait(false);
        if (api is >= Android11ApiLevel)
        {
            _logger.Warning(
                "[Discovery] Legacy TCP ADB atlanıyor — Android 11+ (API {Api}) TLS gerekli",
                api);
            return null;
        }

        const int legacyPort = CompanionPorts.DefaultAdb;
        if (!await IsOpenAsync(target.IpAddress, legacyPort, 350, cts.Token).ConfigureAwait(false))
            return null;

        return new WirelessEndpoint
        {
            IpAddress = target.IpAddress,
            Port = legacyPort,
            Source = EndpointDiscoverySource.Cached.ToString(),
            DeviceId = target.DeviceId,
            DeviceName = target.DeviceName,
            Transport = WirelessTransportKind.LegacyTcp,
            WirelessDebugLikelyEnabled = true
        };
    }

    private CompanionDevice? MatchCompanion(
        IReadOnlyList<CompanionDevice> devices,
        WirelessWatchTarget target)
    {
        if (!string.IsNullOrWhiteSpace(target.DeviceId))
        {
            var byId = devices.FirstOrDefault(d =>
                string.Equals(d.DeviceId, target.DeviceId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
                return byId;
        }

        if (!string.IsNullOrWhiteSpace(target.StableId))
        {
            foreach (var d in devices)
            {
                var sid = _identity.ComputeStableId(d.DeviceId, null, d.Manufacturer, d.DeviceModel);
                if (string.Equals(sid, target.StableId, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
        }

        return devices.FirstOrDefault(d =>
            string.Equals(d.IpAddress, target.IpAddress, StringComparison.OrdinalIgnoreCase));
    }

    private static WirelessDebugDevice? MatchWireless(
        IReadOnlyList<WirelessDebugDevice> devices,
        WirelessWatchTarget target)
    {
        if (!string.IsNullOrWhiteSpace(target.DeviceId))
        {
            var byId = devices.FirstOrDefault(d =>
                string.Equals(d.DeviceId, target.DeviceId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
                return byId;
        }

        return devices.FirstOrDefault(d =>
            string.Equals(d.IpAddress, target.IpAddress, StringComparison.OrdinalIgnoreCase)
            && d.ConnectPort is > 0);
    }

    private static async Task<bool> IsOpenAsync(string ip, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeoutMs);
            await client.ConnectAsync(System.Net.IPAddress.Parse(ip), port, linked.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
