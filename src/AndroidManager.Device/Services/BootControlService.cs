using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Device.Services;

public sealed class BootControlService : IBootControlService
{
    private readonly IAdbService _adb;
    private readonly IDeviceToolsService _tools;
    private readonly ILogger _logger;

    public BootControlService(IAdbService adb, IDeviceToolsService tools, ILogger? logger = null)
    {
        _adb = adb;
        _tools = tools;
        _logger = logger ?? Log.ForContext<BootControlService>();
    }

    public async Task<BootStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var device = _adb.SelectedDevice;
        if (device is null)
        {
            return new BootStatusInfo { IsConnected = false };
        }

        try
        {
            var modeTask = PropAsync("ro.bootmode", cancellationToken);
            var reasonTask = PropAsync("ro.boot.bootreason", cancellationToken);
            var verifiedTask = PropAsync("ro.boot.verifiedbootstate", cancellationToken);
            var lockedTask = PropAsync("ro.boot.flash.locked", cancellationToken);
            var secureTask = PropAsync("ro.secure", cancellationToken);
            var patchTask = PropAsync("ro.build.version.security_patch", cancellationToken);

            await Task.WhenAll(modeTask, reasonTask, verifiedTask, lockedTask, secureTask, patchTask)
                .ConfigureAwait(false);

            var locked = (await lockedTask.ConfigureAwait(false)).Trim();
            return new BootStatusInfo
            {
                IsConnected = true,
                Serial = device.Serial,
                BootMode = Display(await modeTask.ConfigureAwait(false)),
                BootReason = Display(await reasonTask.ConfigureAwait(false)),
                VerifiedBootState = Display(await verifiedTask.ConfigureAwait(false)),
                FlashLocked = locked switch
                {
                    "1" => "Kilitli",
                    "0" => "Açık",
                    _ => Display(locked)
                },
                Secure = Display(await secureTask.ConfigureAwait(false)),
                SecurityPatch = Display(await patchTask.ConfigureAwait(false))
            };
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Boot status query failed");
            return new BootStatusInfo
            {
                IsConnected = true,
                Serial = device.Serial,
                BootMode = "—",
                BootReason = "—"
            };
        }
    }

    public Task<DeviceToolResult> RebootAsync(DeviceRebootMode mode, CancellationToken cancellationToken = default) =>
        _tools.RebootAsync(mode, cancellationToken);

    public async Task<DeviceToolResult> PowerOffAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _adb.ExecuteShellAsync("reboot -p", cancellationToken).ConfigureAwait(false);
            return new DeviceToolResult { Success = true, Message = "Cihaz kapatılıyor…" };
        }
        catch (Exception ex)
        {
            try
            {
                await _adb.ExecuteShellAsync("svc power shutdown", cancellationToken).ConfigureAwait(false);
                return new DeviceToolResult { Success = true, Message = "Cihaz kapatılıyor (svc)…" };
            }
            catch
            {
                return new DeviceToolResult { Success = false, Message = ex.Message };
            }
        }
    }

    public async Task<DeviceToolResult> SoftRebootAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Soft restart of Android framework (often needs elevated shell / root)
            await _adb.ExecuteShellAsync("setprop ctl.restart zygote", cancellationToken).ConfigureAwait(false);
            return new DeviceToolResult
            {
                Success = true,
                Message = "Yumuşak yeniden başlatma istendi (zygote). Root gerekebilir."
            };
        }
        catch (Exception ex)
        {
            return new DeviceToolResult { Success = false, Message = ex.Message };
        }
    }

    private async Task<string> PropAsync(string name, CancellationToken ct)
    {
        try
        {
            return await _adb.ExecuteShellAsync($"getprop {name}", ct).ConfigureAwait(false);
        }
        catch
        {
            return "";
        }
    }

    private static string Display(string value)
    {
        var t = value.Trim();
        return string.IsNullOrWhiteSpace(t) ? "—" : t;
    }
}
