using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Device.Services;

public sealed class DeviceToolsService : IDeviceToolsService
{
    private const int MaxClipboardChars = 200_000;
    private const string ClipboardRemotePath = "/sdcard/am_clipboard.txt";

    private readonly IAdbService _adb;
    private readonly IAdbSyncService _sync;
    private readonly IDeviceInfoService _deviceInfo;
    private readonly ILogger _logger;

    public DeviceToolsService(
        IAdbService adb,
        IAdbSyncService sync,
        IDeviceInfoService deviceInfo,
        ILogger? logger = null)
    {
        _adb = adb;
        _sync = sync;
        _deviceInfo = deviceInfo;
        _logger = logger ?? Log.ForContext<DeviceToolsService>();
    }

    public async Task<DeviceToolResult> RebootAsync(DeviceRebootMode mode, CancellationToken cancellationToken = default)
    {
        var target = mode switch
        {
            DeviceRebootMode.Recovery => "recovery",
            DeviceRebootMode.Bootloader => "bootloader",
            _ => null
        };

        var transport = await _adb.RebootTransportAsync(target, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (transport.Success)
        {
            return Ok(mode switch
            {
                DeviceRebootMode.Recovery => "Recovery’ye yeniden başlatılıyor…",
                DeviceRebootMode.Bootloader => "Bootloader’a yeniden başlatılıyor…",
                _ => "Cihaz yeniden başlatılıyor…"
            });
        }

        try
        {
            var cmd = string.IsNullOrEmpty(target) ? "reboot" : $"reboot {target}";
            await _adb.ExecuteShellAsync(cmd, cancellationToken).ConfigureAwait(false);
            return Ok("Yeniden başlatılıyor…");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Reboot failed");
            return Fail(transport.Message + Environment.NewLine + ex.Message);
        }
    }

    public async Task<DeviceToolResult> LockScreenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _adb.ExecuteShellAsync("input keyevent 26", cancellationToken).ConfigureAwait(false);
            return Ok("Ekran kilitlendi");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    public async Task<DeviceToolResult> SetStayAwakeAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            // While charging / USB plugged — classic developer stay-awake bits.
            var value = enabled ? "7" : "0";
            await _adb.ExecuteShellAsync(
                $"settings put global stay_on_while_plugged_in {value}",
                cancellationToken).ConfigureAwait(false);
            return Ok(enabled ? "Ekran açık tutulacak (USB/şarj bağlıyken)" : "Stay-awake kapatıldı");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    public async Task<DeviceToolResult> SetWifiEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            await _adb.ExecuteShellAsync(enabled ? "svc wifi enable" : "svc wifi disable", cancellationToken)
                .ConfigureAwait(false);
            return Ok(enabled ? "Wi‑Fi açıldı" : "Wi‑Fi kapatıldı");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    public async Task<DeviceToolResult> SetBluetoothEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            await _adb.ExecuteShellAsync(enabled ? "svc bluetooth enable" : "svc bluetooth disable", cancellationToken)
                .ConfigureAwait(false);
            return Ok(enabled ? "Bluetooth açıldı" : "Bluetooth kapatıldı");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    public async Task<DeviceToolResult> CaptureScreenshotAsync(
        string? saveDirectory = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var dir = saveDirectory;
            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                    "AndroidManager");
            }

            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var remote = $"/sdcard/Pictures/am_shot_{stamp}.png";
            await _adb.ExecuteShellAsync("mkdir -p /sdcard/Pictures", cancellationToken).ConfigureAwait(false);
            await _adb.ExecuteShellAsync($"screencap -p \"{remote}\"", cancellationToken).ConfigureAwait(false);

            var local = Path.Combine(dir, Path.GetFileName(remote));
            try
            {
                await _sync.PullAsync(remote, local, null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception pullEx)
            {
                _logger.Debug(pullEx, "Sync pull failed, trying base64");
                if (!await TryPullViaShellAsync(remote, local, cancellationToken).ConfigureAwait(false))
                    return Ok($"Ekran görüntüsü cihazda: {remote}", remote);
            }

            if (!File.Exists(local) || new FileInfo(local).Length == 0)
                return Ok($"Ekran görüntüsü cihazda: {remote}", remote);

            try
            {
                await _adb.ExecuteShellAsync($"rm -f \"{remote}\"", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception rmEx)
            {
                _logger.Debug(rmEx, "Could not remove remote screenshot");
            }

            return Ok($"Ekran görüntüsü kaydedildi: {local}", local);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Screenshot failed");
            return Fail(ex.Message);
        }
    }

    public async Task<DeviceToolResult> GetClipboardAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var cmdRaw = await SafeShellAsync(
                "cmd clipboard get-primary-clip 2>/dev/null",
                cancellationToken).ConfigureAwait(false);

            var text = ExtractClipboardText(cmdRaw);
            if (!string.IsNullOrWhiteSpace(text) &&
                !LooksLikeClipboardFailure(cmdRaw))
            {
                return Ok("Clipboard alındı", text);
            }

            var dump = await SafeShellAsync("dumpsys clipboard 2>/dev/null", cancellationToken)
                .ConfigureAwait(false);
            text = ExtractClipboardText(dump);
            if (!string.IsNullOrWhiteSpace(text) && text.Length < MaxClipboardChars)
                return Ok("Clipboard alındı (dumpsys)", text);

            var file = await SafeShellAsync(
                $"cat {ClipboardRemotePath} 2>/dev/null; " +
                "cat /sdcard/Android/data/com.androidmanager.companion/files/am_clipboard.txt 2>/dev/null",
                cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(file))
                return Ok("Clipboard (dosya köprüsü)", file.Trim());

            return Fail(
                "Clipboard okunamadı. Üretici kısıtı olabilir. " +
                "«Clipboard → Android» dosya köprüsünü kullanın.");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    public async Task<DeviceToolResult> SetClipboardAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
            return Fail("Boş metin");

        if (text.Length > MaxClipboardChars)
            return Fail($"Metin çok uzun (max {MaxClipboardChars:N0} karakter)");

        var temp = Path.Combine(Path.GetTempPath(), $"am_clip_{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
                .ConfigureAwait(false);

            // Reliable bridge for all OEM builds
            await _sync.PushAsync(temp, ClipboardRemotePath, null, cancellationToken).ConfigureAwait(false);

            // Best-effort system clipboard (often blocked on Android 10+)
            var escaped = EscapeForSingleQuotes(text.Length > 4000 ? text[..4000] : text);
            var cmdOut = await SafeShellAsync(
                $"cmd clipboard set-primary-clip text/plain '{escaped}' 2>/dev/null; echo EXIT:$?",
                cancellationToken).ConfigureAwait(false);

            var systemSet = !LooksLikeClipboardFailure(cmdOut) &&
                            !cmdOut.Contains("EXIT:1", StringComparison.Ordinal) &&
                            !cmdOut.Contains("EXIT:255", StringComparison.Ordinal);

            return Ok(
                systemSet
                    ? "Clipboard Android’e gönderildi"
                    : "Pano dosyaya yazıldı (/sdcard/am_clipboard.txt). Sistem panosu ADB ile kilitli olabilir.",
                text);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* ignore */ }
        }
    }

    public async Task<LiveDeviceMetrics> GetLiveMetricsAsync(CancellationToken cancellationToken = default)
    {
        var batteryTask = _deviceInfo.GetBatteryInfoAsync(cancellationToken);
        var ramTask = _deviceInfo.GetRamInfoAsync(cancellationToken);
        var cpuTask = _deviceInfo.GetCpuUsageAsync(cancellationToken);
        var ipTask = SafeShellAsync(
            "ip -f inet addr show wlan0 2>/dev/null | grep -oE 'inet [0-9.]+' | awk '{print $2}' | head -n1",
            cancellationToken);
        var wifiTask = SafeShellAsync(
            "dumpsys wifi 2>/dev/null | grep -m3 -E 'mWifiInfo|SSID:|RSS[Ii]:'",
            cancellationToken);
        var patchTask = SafeShellAsync("getprop ro.build.version.security_patch", cancellationToken);
        var lockTask = SafeShellAsync("getprop ro.boot.flash.locked", cancellationToken);

        await Task.WhenAll(batteryTask, ramTask, cpuTask, ipTask, wifiTask, patchTask, lockTask)
            .ConfigureAwait(false);

        var battery = await batteryTask.ConfigureAwait(false);
        var ram = await ramTask.ConfigureAwait(false);
        var cpu = await cpuTask.ConfigureAwait(false);
        var ip = (await ipTask.ConfigureAwait(false)).Trim();
        var wifiRaw = await wifiTask.ConfigureAwait(false);
        var patch = (await patchTask.ConfigureAwait(false)).Trim();
        var locked = (await lockTask.ConfigureAwait(false)).Trim();

        ParseWifi(wifiRaw, out var ssid, out var rssi);

        return new LiveDeviceMetrics
        {
            CapturedAt = DateTime.Now,
            CpuPercent = Math.Clamp(cpu.UsagePercent, 0, 100),
            RamPercent = Math.Clamp(ram.UsagePercent, 0, 100),
            RamTotalKb = ram.TotalKb,
            RamAvailableKb = ram.AvailableKb,
            BatteryPercent = Math.Clamp(battery.Level, 0, 100),
            TemperatureC = battery.Temperature,
            IsCharging = battery.IsCharging,
            VoltageMv = battery.Voltage,
            WifiIp = ip,
            WifiSsid = ssid,
            WifiRssi = rssi,
            SecurityPatch = string.IsNullOrWhiteSpace(patch) ? "—" : patch,
            BootloaderLocked = locked switch
            {
                "1" => "Kilitli",
                "0" => "Açık",
                _ => string.IsNullOrWhiteSpace(locked) ? "—" : locked
            }
        };
    }

    private static void ParseWifi(string wifiRaw, out string ssid, out int? rssi)
    {
        ssid = "";
        rssi = null;
        if (string.IsNullOrWhiteSpace(wifiRaw))
            return;

        var ssidMatch = Regex.Match(wifiRaw, @"SSID:\s*""?([^""\r\n,]+)");
        if (ssidMatch.Success)
        {
            var value = ssidMatch.Groups[1].Value.Trim();
            if (!value.Equals("<unknown ssid>", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("0x", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                ssid = value;
            }
        }

        var rssiMatch = Regex.Match(wifiRaw, @"RSS[Ii]:\s*(-?\d+)");
        if (rssiMatch.Success && int.TryParse(rssiMatch.Groups[1].Value, out var r))
            rssi = r;
    }

    private async Task<bool> TryPullViaShellAsync(string remote, string local, CancellationToken ct)
    {
        try
        {
            var b64 = await _adb.ExecuteShellAsync(
                $"base64 \"{remote}\" 2>/dev/null | tr -d '\\n\\r '", ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(b64) || b64.Length < 32)
                return false;

            var bytes = Convert.FromBase64String(b64.Trim());
            await File.WriteAllBytesAsync(local, bytes, ct).ConfigureAwait(false);
            return File.Exists(local) && new FileInfo(local).Length > 0;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Screenshot pull via base64 failed");
            return false;
        }
    }

    private async Task<string> SafeShellAsync(string command, CancellationToken cancellationToken)
    {
        try
        {
            return await _adb.ExecuteShellAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Optional shell command failed: {Command}", command);
            return string.Empty;
        }
    }

    private static bool LooksLikeClipboardFailure(string raw) =>
        string.IsNullOrWhiteSpace(raw) ||
        raw.Contains("Unknown command", StringComparison.OrdinalIgnoreCase) ||
        raw.Contains("Permission Denial", StringComparison.OrdinalIgnoreCase) ||
        raw.Contains("SecurityException", StringComparison.OrdinalIgnoreCase) ||
        raw.Contains("__CLIP_FAIL__", StringComparison.Ordinal);

    private static string ExtractClipboardText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || LooksLikeClipboardFailure(raw))
            return "";

        // ClipData { text/plain {T:"hello"} } variants
        var typed = Regex.Match(raw, @"\{T:""((?:\\.|[^""])*)""\}");
        if (typed.Success)
            return UnescapeJava(typed.Groups[1].Value);

        var quoted = Regex.Match(raw, "text/plain[^\"]*\"((?:\\\\.|[^\"])*)\"");
        if (quoted.Success)
            return UnescapeJava(quoted.Groups[1].Value);

        var any = Regex.Match(raw, "\"([^\"]{1,4000})\"");
        if (any.Success)
            return UnescapeJava(any.Groups[1].Value);

        var trimmed = raw.Trim();
        if (trimmed.Contains("ClipData", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("ClipboardService", StringComparison.OrdinalIgnoreCase))
            return "";

        return trimmed.Length > MaxClipboardChars ? trimmed[..MaxClipboardChars] : trimmed;
    }

    private static string UnescapeJava(string value) =>
        value.Replace("\\\"", "\"", StringComparison.Ordinal)
             .Replace("\\n", "\n", StringComparison.Ordinal)
             .Replace("\\t", "\t", StringComparison.Ordinal);

    private static string EscapeForSingleQuotes(string text) =>
        text.Replace("'", "'\\''", StringComparison.Ordinal);

    private static DeviceToolResult Ok(string message, string? payload = null) => new()
    {
        Success = true,
        Message = message,
        PathOrPayload = payload
    };

    private static DeviceToolResult Fail(string message) => new()
    {
        Success = false,
        Message = message
    };
}
