using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Device.Services;

public sealed class WirelessConnectionOrchestrator : IWirelessConnectionOrchestrator
{
    private readonly IWirelessDiscoveryChain _chain;
    private readonly IUsbConnectionMonitor _usbMonitor;
    private readonly IRootWirelessDebugService _rootWireless;
    private readonly IDevicePresenceStore _presence;
    private readonly ILogger _logger;

    public WirelessConnectionOrchestrator(
        IWirelessDiscoveryChain chain,
        IUsbConnectionMonitor usbMonitor,
        IRootWirelessDebugService rootWireless,
        IDevicePresenceStore presence,
        ILogger? logger = null)
    {
        _chain = chain;
        _usbMonitor = usbMonitor;
        _rootWireless = rootWireless;
        _presence = presence;
        _logger = logger ?? Log.ForContext<WirelessConnectionOrchestrator>();
    }

    public async Task<WirelessEndpoint?> ResolveAsync(
        WirelessWatchTarget target,
        CancellationToken cancellationToken = default)
    {
        var wifiTask = _chain.ResolveWiFiAsync(target, cancellationToken);
        var usbTask = TryUsbBootstrapAsync(target, cancellationToken);

        var first = await Task.WhenAny(wifiTask, usbTask).ConfigureAwait(false);

        if (first == wifiTask)
        {
            var wifi = await wifiTask.ConfigureAwait(false);
            if (wifi is not null)
                return wifi;

            return await usbTask.ConfigureAwait(false);
        }

        var usb = await usbTask.ConfigureAwait(false);
        if (usb is not null)
            return usb;

        return await wifiTask.ConfigureAwait(false);
    }

    public WirelessDiagnosticScenario InferScenario(
        WirelessWatchTarget target,
        DevicePresenceRecord? presence)
    {
        if (presence is null || presence.IsStale(TimeSpan.FromMinutes(3)))
            return WirelessDiagnosticScenario.DeviceOffline;

        if (string.Equals(presence.LastNetworkType, "cellular", StringComparison.OrdinalIgnoreCase))
            return WirelessDiagnosticScenario.DeviceOnCellular;

        if (presence.LastCompanionOnline && !presence.IsStale(TimeSpan.FromSeconds(45)))
            return WirelessDiagnosticScenario.WirelessDebugOff;

        if (presence.LastKnownPort is null or 0)
            return WirelessDiagnosticScenario.PortUnreadable;

        return WirelessDiagnosticScenario.Unknown;
    }

    private async Task<WirelessEndpoint?> TryUsbBootstrapAsync(
        WirelessWatchTarget target,
        CancellationToken cancellationToken)
    {
        try
        {
            var link = await _usbMonitor.FindUsbLinkAsync(target, cancellationToken).ConfigureAwait(false);
            if (link is null)
                return null;

            if (!await _rootWireless.IsRootAvailableAsync(cancellationToken).ConfigureAwait(false))
                return null;

            var info = await _rootWireless.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (info.TlsPort is not > 0)
                return null;

            var ip = info.WifiIp ?? target.IpAddress;
            if (string.IsNullOrWhiteSpace(ip))
                return null;

            _logger.Information(
                "[Orchestrator] USB+root bootstrap {Ip}:{Port} for {Stable}",
                ip, info.TlsPort, target.StableId);

            _presence.RecordEndpoint(target.StableId, ip, info.TlsPort!.Value, "wifi");

            return new WirelessEndpoint
            {
                IpAddress = ip,
                Port = info.TlsPort!.Value,
                Source = EndpointDiscoverySource.Root.ToString(),
                DeviceId = target.DeviceId,
                DeviceName = target.DeviceName,
                Transport = WirelessTransportKind.WirelessTls,
                WirelessDebugLikelyEnabled = true
            };
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[Orchestrator] USB bootstrap failed");
            return null;
        }
    }
}
