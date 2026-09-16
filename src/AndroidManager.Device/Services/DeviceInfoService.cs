using System.IO;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Device.Parsing;
using Serilog;

namespace AndroidManager.Device.Services;

public sealed class DeviceInfoService : IDeviceInfoService
{
    private readonly IAdbService _adbService;
    private readonly ILogger _logger;

    public DeviceInfoService(IAdbService adbService, ILogger? logger = null)
    {
        _adbService = adbService;
        _logger = logger ?? Log.ForContext<DeviceInfoService>();
    }

    public async Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken cancellationToken = default)
    {
        var info = new DeviceInfo();

        var manufacturerTask = GetPropertyAsync("ro.product.manufacturer", cancellationToken);
        var modelTask = GetPropertyAsync("ro.product.model", cancellationToken);
        var versionTask = GetPropertyAsync("ro.build.version.release", cancellationToken);
        var apiTask = GetPropertyAsync("ro.build.version.sdk", cancellationToken);
        var serialTask = GetPropertyAsync("ro.serialno", cancellationToken);
        var batteryTask = GetBatteryInfoAsync(cancellationToken);
        var storageTask = GetStorageInfoAsync(cancellationToken);
        var ramTask = GetRamInfoAsync(cancellationToken);
        var cpuTask = GetCpuUsageAsync(cancellationToken);

        await Task.WhenAll(
                manufacturerTask,
                modelTask,
                versionTask,
                apiTask,
                serialTask,
                batteryTask,
                storageTask,
                ramTask,
                cpuTask)
            .ConfigureAwait(false);

        info.Manufacturer = await manufacturerTask.ConfigureAwait(false);
        info.Model = await modelTask.ConfigureAwait(false);
        info.AndroidVersion = await versionTask.ConfigureAwait(false);
        info.ApiLevel = await apiTask.ConfigureAwait(false);
        var serialResult = await serialTask.ConfigureAwait(false);
        info.Serial = string.IsNullOrWhiteSpace(serialResult)
            ? _adbService.SelectedDevice?.Serial ?? string.Empty
            : serialResult;
        info.Battery = await batteryTask.ConfigureAwait(false);
        info.Storage = await storageTask.ConfigureAwait(false);
        info.Ram = await ramTask.ConfigureAwait(false);
        info.Cpu = await cpuTask.ConfigureAwait(false);

        return info;
    }

    public async Task<BatteryInfo> GetBatteryInfoAsync(CancellationToken cancellationToken = default)
    {
        var output = await _adbService.ExecuteShellAsync("dumpsys battery", cancellationToken).ConfigureAwait(false);
        return DeviceOutputParsers.ParseBattery(output);
    }

    public async Task<StorageInfo> GetStorageInfoAsync(CancellationToken cancellationToken = default)
    {
        var output = await _adbService.ExecuteShellAsync("df /sdcard", cancellationToken).ConfigureAwait(false);
        return DeviceOutputParsers.ParseDf(output);
    }

    public async Task<RamInfo> GetRamInfoAsync(CancellationToken cancellationToken = default)
    {
        var output = await _adbService.ExecuteShellAsync("cat /proc/meminfo", cancellationToken).ConfigureAwait(false);
        return DeviceOutputParsers.ParseMemInfo(output);
    }

    public async Task<CpuInfo> GetCpuUsageAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var output = await _adbService.ExecuteShellAsync("top -bn1 -m 1", cancellationToken).ConfigureAwait(false);
            return new CpuInfo { UsagePercent = DeviceOutputParsers.ParseCpuUsage(output) };
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "CPU usage query failed");
            return new CpuInfo();
        }
    }

    public async Task<DeviceDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var batteryTask = GetBatteryInfoAsync(cancellationToken);
        var storageTask = GetStorageInfoAsync(cancellationToken);
        var ramTask = GetRamInfoAsync(cancellationToken);
        var cpuTask = GetCpuUsageAsync(cancellationToken);
        var batteryRawTask = _adbService.ExecuteShellAsync("dumpsys battery", cancellationToken);
        var sensorsTask = SafeShellAsync("dumpsys sensorservice", cancellationToken);
        var cycleTask = SafeShellAsync(
            "for f in /sys/class/power_supply/*/cycle_count; do [ -f \"$f\" ] && cat \"$f\" && break; done",
            cancellationToken);

        await Task.WhenAll(
                batteryTask,
                storageTask,
                ramTask,
                cpuTask,
                batteryRawTask,
                sensorsTask,
                cycleTask)
            .ConfigureAwait(false);

        var battery = await batteryTask.ConfigureAwait(false);
        var cycleCount = DeviceOutputParsers.ParseCycleCount(await cycleTask.ConfigureAwait(false));
        var batteryRawResult = await batteryRawTask.ConfigureAwait(false);
        if (cycleCount == 0)
            cycleCount = DeviceOutputParsers.ParseIntAfterKey(batteryRawResult, "Cycle count:");

        return new DeviceDiagnostics
        {
            Battery = battery,
            Storage = await storageTask.ConfigureAwait(false),
            Ram = await ramTask.ConfigureAwait(false),
            Cpu = await cpuTask.ConfigureAwait(false),
            ChargeCounter = battery.ChargeCounter,
            CycleCount = cycleCount,
            Technology = battery.Technology,
            Status = battery.Status,
            Sensors = DeviceOutputParsers.ParseSensors(await sensorsTask.ConfigureAwait(false)),
            RawBatteryDump = batteryRawResult,
            CapturedAt = DateTime.Now
        };
    }

    public async Task<string> SaveDiagnosticsReportAsync(
        DeviceDiagnostics diagnostics,
        CancellationToken cancellationToken = default)
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var dir = Path.Combine(docs, "AndroidManager", "Diagnostics");
        Directory.CreateDirectory(dir);

        var stamp = diagnostics.CapturedAt.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(dir, $"diagnostics-{stamp}.txt");
        var body = diagnostics.SummaryText;
        if (!string.IsNullOrWhiteSpace(diagnostics.RawBatteryDump))
            body += "\n--- dumpsys battery ---\n" + diagnostics.RawBatteryDump.Trim() + "\n";

        await File.WriteAllTextAsync(path, body, cancellationToken).ConfigureAwait(false);
        _logger.Information("Diagnostics report saved to {Path}", path);
        return path;
    }

    private async Task<string> SafeShellAsync(string command, CancellationToken cancellationToken)
    {
        try
        {
            return await _adbService.ExecuteShellAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Optional diagnostics command failed: {Command}", command);
            return string.Empty;
        }
    }

    private async Task<string> GetPropertyAsync(string property, CancellationToken cancellationToken)
    {
        var output = await _adbService.ExecuteShellAsync($"getprop {property}", cancellationToken).ConfigureAwait(false);
        return output.Trim();
    }
}
