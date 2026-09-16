using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Device.Services;

public sealed class WirelessDebugRecoveryService : IWirelessDebugRecoveryService
{
    private readonly IAdbService _adb;
    private readonly IRootWirelessDebugService _rootWireless;
    private readonly IUsbConnectionMonitor _usbMonitor;
    private readonly IWirelessConnectionOrchestrator _orchestrator;
    private readonly IDevicePresenceStore _presence;
    private readonly ICompanionBatteryGuard _batteryGuard;
    private readonly ILogger _logger;

    public WirelessDebugRecoveryService(
        IAdbService adb,
        IRootWirelessDebugService rootWireless,
        IUsbConnectionMonitor usbMonitor,
        IWirelessConnectionOrchestrator orchestrator,
        IDevicePresenceStore presence,
        ICompanionBatteryGuard batteryGuard,
        ILogger? logger = null)
    {
        _adb = adb;
        _rootWireless = rootWireless;
        _usbMonitor = usbMonitor;
        _orchestrator = orchestrator;
        _presence = presence;
        _batteryGuard = batteryGuard;
        _logger = logger ?? Log.ForContext<WirelessDebugRecoveryService>();
    }

    public async Task<bool> TryRecoverViaUsbAsync(
        WirelessWatchTarget target,
        CancellationToken cancellationToken = default)
    {
        var link = await _usbMonitor.FindUsbLinkAsync(target, cancellationToken).ConfigureAwait(false);
        if (link is null)
        {
            _logger.Warning("[UsbRecover] No USB device for {Stable}", target.StableId);
            return false;
        }

        await _batteryGuard.TryEnsureCompanionCanRunInBackgroundAsync(link.Serial, cancellationToken)
            .ConfigureAwait(false);

        if (await _rootWireless.IsRootAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            var info = await _rootWireless.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (info.TlsPort is > 0 && !string.IsNullOrWhiteSpace(info.WifiIp))
            {
                var ok = await ConnectAfterWirelessDebugOpenAsync(
                    link.Serial,
                    info.WifiIp!,
                    info.TlsPort!.Value,
                    cancellationToken).ConfigureAwait(false);
                if (ok)
                {
                    _presence.RecordEndpoint(target.StableId, info.WifiIp!, info.TlsPort!.Value, "wifi");
                    return true;
                }
            }

            var api = await _rootWireless.GetDeviceApiLevelAsync(cancellationToken).ConfigureAwait(false);
            if (api is < 30)
            {
                _logger.Information("[UsbRecover] Root — deneysel WD açma (API {Api})", api);
                var enable = await _rootWireless.TryEnableWirelessDebugAsync(cancellationToken).ConfigureAwait(false);
                if (enable is WdEnableResult.MaybeEnabled or WdEnableResult.PortAlreadyOpen)
                {
                    info = await _rootWireless.ProbeAsync(cancellationToken).ConfigureAwait(false);
                    if (info.TlsPort is > 0 && !string.IsNullOrWhiteSpace(info.WifiIp))
                    {
                        var ok = await ConnectAfterWirelessDebugOpenAsync(
                            link.Serial,
                            info.WifiIp!,
                            info.TlsPort!.Value,
                            cancellationToken).ConfigureAwait(false);
                        if (ok)
                        {
                            _presence.RecordEndpoint(target.StableId, info.WifiIp!, info.TlsPort!.Value, "wifi");
                            return true;
                        }
                    }
                }
            }
            else
            {
                _logger.Information(
                    "[UsbRecover] Root port okuma — WD açma Android 11+ güvenilir değil (API {Api})",
                    api);
            }
        }

        await _adb.ExecuteShellAsync(
            "am start -a android.settings.WIRELESS_DEBUGGING_SETTINGS 2>/dev/null || " +
            "am start -a android.settings.APPLICATION_DEVELOPMENT_SETTINGS",
            cancellationToken).ConfigureAwait(false);

        var usbSerial = link.Serial;
        int? port = null;
        for (var i = 0; i < 80 && !cancellationToken.IsCancellationRequested; i++)
        {
            port = await PollTlsPortAsync(usbSerial, cancellationToken).ConfigureAwait(false);
            if (port is > 0)
                break;
            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        }

        if (port is not > 0)
        {
            _logger.Warning("[UsbRecover] WD toggle not opened within timeout");
            return false;
        }

        var wifiIp = await _rootWireless.GetWifiIpAsync(cancellationToken).ConfigureAwait(false)
                     ?? target.IpAddress;
        if (string.IsNullOrWhiteSpace(wifiIp))
            wifiIp = await _adb.GetDeviceIpAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(wifiIp))
            return false;

        var connected = await ConnectAfterWirelessDebugOpenAsync(
            usbSerial,
            wifiIp,
            port!.Value,
            cancellationToken).ConfigureAwait(false);

        if (connected)
            _presence.RecordEndpoint(target.StableId, wifiIp, port!.Value, "wifi");

        return connected;
    }

    public async Task<bool> ConnectAfterWirelessDebugOpenAsync(
        string usbSerial,
        string wifiIp,
        int port,
        CancellationToken cancellationToken = default)
    {
        for (var retry = 0; retry < 10 && !cancellationToken.IsCancellationRequested; retry++)
        {
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

            if (await _adb.ConnectWifiAsync(wifiIp, port, cancellationToken).ConfigureAwait(false))
            {
                _logger.Information("[UsbRecover] Connected {Ip}:{Port} after retry {N}", wifiIp, port, retry);
                return true;
            }

            _logger.Debug("[UsbRecover] Connect retry {N} for {Ip}:{Port}", retry + 1, wifiIp, port);
        }

        return false;
    }

    private async Task<int?> PollTlsPortAsync(string usbSerial, CancellationToken ct)
    {
        try
        {
            var output = await _adb.ExecuteShellAsync(
                "getprop service.adb.tls.port",
                ct).ConfigureAwait(false);
            if (int.TryParse(output.Trim(), out var port) && port > 0)
                return port;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[UsbRecover] getprop poll failed");
        }

        if (await _rootWireless.IsRootAvailableAsync(ct).ConfigureAwait(false))
            return await _rootWireless.GetTlsPortAsync(ct).ConfigureAwait(false);

        return null;
    }
}
