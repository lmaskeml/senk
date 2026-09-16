using System.Globalization;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Device.Services;

/// <summary>
/// Root ile güvenilir: TLS port okuma (getprop). WD açma cihaza bağlı — garanti verilmez.
/// </summary>
public sealed class RootWirelessDebugService : IRootWirelessDebugService
{
    private const int Android11ApiLevel = 30;

    private readonly IAdbService _adb;
    private readonly IElevatedShellService? _elevated;
    private readonly ILogger _logger;

    public RootWirelessDebugService(
        IAdbService adb,
        IElevatedShellService? elevated = null,
        ILogger? logger = null)
    {
        _adb = adb;
        _elevated = elevated;
        _logger = logger ?? Log.ForContext<RootWirelessDebugService>();
    }

    public async Task<bool> IsRootAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (_elevated is null)
            return false;

        try
        {
            return await _elevated.IsAvailableAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    public async Task<int?> GetDeviceApiLevelAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var raw = await _adb.ExecuteShellAsync("getprop ro.build.version.sdk", cancellationToken)
                .ConfigureAwait(false);
            return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var api)
                ? api
                : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<RootWirelessDebugInfo> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var root = await IsRootAvailableAsync(cancellationToken).ConfigureAwait(false);
        if (!root)
            return new RootWirelessDebugInfo { RootAvailable = false };

        var enabled = await ReadBoolPropAsync("persist.adb.tls_server.enable", cancellationToken)
            .ConfigureAwait(false);
        var port = await GetTlsPortAsync(cancellationToken).ConfigureAwait(false);
        var wifiIp = await GetWifiIpAsync(cancellationToken).ConfigureAwait(false);
        var api = await GetDeviceApiLevelAsync(cancellationToken).ConfigureAwait(false);
        var legacy = api is < Android11ApiLevel
            ? await ReadIntPropAsync("service.adb.tcp.port", cancellationToken).ConfigureAwait(false)
            : null;

        return new RootWirelessDebugInfo
        {
            RootAvailable = true,
            WirelessDebugEnabled = enabled || port is > 0,
            TlsPort = port,
            WifiIp = wifiIp,
            LegacyTcpEnabled = legacy is > 0
        };
    }

    public async Task<int?> GetTlsPortAsync(CancellationToken cancellationToken = default)
    {
        foreach (var key in new[] { "service.adb.tls.port", "persist.adb.tls_server.port" })
        {
            var port = await ReadIntPropAsync(key, cancellationToken).ConfigureAwait(false);
            if (port is > 0)
                return port;
        }

        return null;
    }

    public async Task<string?> GetWifiIpAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsRootAvailableAsync(cancellationToken).ConfigureAwait(false))
            return await _adb.GetDeviceIpAsync(cancellationToken).ConfigureAwait(false);

        var output = await RunRootAsync(
            "ip -f inet addr show wlan0 2>/dev/null | awk '/inet /{print $2}' | cut -d/ -f1 | head -1",
            cancellationToken).ConfigureAwait(false);

        var ip = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (!string.IsNullOrWhiteSpace(ip) && ip.Contains('.'))
            return ip;

        return await _adb.GetDeviceIpAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<WdEnableResult> TryEnableWirelessDebugAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsRootAvailableAsync(cancellationToken).ConfigureAwait(false))
            return WdEnableResult.Failed;

        var api = await GetDeviceApiLevelAsync(cancellationToken).ConfigureAwait(false);
        if (api is >= Android11ApiLevel)
        {
            _logger.Warning(
                "[RootWD] WD açma root ile Android 11+ (API {Api}) üzerinde güvenilir değil — kullanıcı toggle gerekli",
                api);
            return WdEnableResult.NotSupported;
        }

        var existing = await GetTlsPortAsync(cancellationToken).ConfigureAwait(false);
        if (existing is > 0)
            return WdEnableResult.PortAlreadyOpen;

        _logger.Information("[RootWD] Deneysel WD ayarı (Android 10 ve altı)");
        try
        {
            await RunRootAsync("settings put global adb_wifi_enabled 1", cancellationToken).ConfigureAwait(false);
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            if ((await GetTlsPortAsync(cancellationToken).ConfigureAwait(false)) is > 0)
                return WdEnableResult.MaybeEnabled;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[RootWD] Experimental enable failed");
        }

        return WdEnableResult.Failed;
    }

    public async Task<bool> TryEnableLegacyTcpAdbAsync(
        int port = 5555,
        CancellationToken cancellationToken = default)
    {
        if (!await IsRootAvailableAsync(cancellationToken).ConfigureAwait(false))
            return false;

        var api = await GetDeviceApiLevelAsync(cancellationToken).ConfigureAwait(false);
        if (api is >= Android11ApiLevel)
        {
            _logger.Warning(
                "[RootWD] Legacy TCP ADB Android 11+ (API {Api}) desteklenmiyor — TLS gerekli",
                api);
            return false;
        }

        _logger.Information("[RootWD] Enabling legacy TCP ADB on port {Port} (API {Api})", port, api);
        await RunRootAsync(
            $"setprop service.adb.tcp.port {port} && stop adbd && start adbd",
            cancellationToken).ConfigureAwait(false);

        await Task.Delay(800, cancellationToken).ConfigureAwait(false);
        var actual = await ReadIntPropAsync("service.adb.tcp.port", cancellationToken).ConfigureAwait(false);
        return actual == port;
    }

    private async Task<int?> ReadIntPropAsync(string key, CancellationToken ct)
    {
        var raw = await ReadPropAsync(key, ct).ConfigureAwait(false);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0
            ? v
            : null;
    }

    private async Task<bool> ReadBoolPropAsync(string key, CancellationToken ct)
    {
        var raw = await ReadPropAsync(key, ct).ConfigureAwait(false);
        return raw is "1" or "true";
    }

    private async Task<string> ReadPropAsync(string key, CancellationToken ct)
    {
        if (await IsRootAvailableAsync(ct).ConfigureAwait(false))
        {
            var rootVal = await RunRootAsync($"getprop {key}", ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(rootVal) && rootVal is not "0")
                return rootVal.Trim();
        }

        try
        {
            return (await _adb.ExecuteShellAsync($"getprop {key}", ct).ConfigureAwait(false)).Trim();
        }
        catch
        {
            return "";
        }
    }

    private async Task<string> RunRootAsync(string command, CancellationToken ct)
    {
        if (_elevated is null)
            return "";

        try
        {
            return await _elevated.RunAsync(command, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[RootWD] Root command failed: {Cmd}", command);
            return "";
        }
    }
}
