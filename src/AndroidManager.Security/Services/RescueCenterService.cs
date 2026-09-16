using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Security.Services;

public sealed class RescueCenterService : IRescueCenterService
{
    private readonly IAdbService _adb;
    private readonly IDeviceToolsService _tools;
    private readonly IPlatformRepository _platform;
    private readonly IDeviceIdentityService _identity;
    private readonly ILogger _logger;

    private static readonly string RootBackupDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AndroidManager", "Root");

    public RescueCenterService(
        IAdbService adb,
        IDeviceToolsService tools,
        IPlatformRepository platform,
        IDeviceIdentityService identity,
        ILogger? logger = null)
    {
        _adb = adb;
        _tools = tools;
        _platform = platform;
        _identity = identity;
        _logger = logger ?? Log.ForContext<RescueCenterService>();
    }

    public async Task<RescueSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var adbMode = _adb.SelectedDevice is not null
            ? DeviceConnectionMode.Adb
            : DeviceConnectionMode.Offline;

        var fastbootDevices = await GetFastbootDevicesAsync(cancellationToken).ConfigureAwait(false);
        var serialHint = _adb.SelectedDevice?.Serial;
        var fastbootMode = fastbootDevices.Count > 0
            ? DeviceConnectionMode.Fastboot
            : DeviceConnectionMode.Offline;

        // Prefer matching selected serial when present.
        if (!string.IsNullOrWhiteSpace(serialHint)
            && await IsDeviceInFastbootAsync(serialHint, cancellationToken).ConfigureAwait(false))
        {
            fastbootMode = DeviceConnectionMode.Fastboot;
        }

        var recoveryMode = DeviceConnectionMode.Unknown;
        if (adbMode == DeviceConnectionMode.Adb && serialHint is not null)
        {
            try
            {
                var state = await _adb.ExecuteShellAsync("getprop ro.bootmode 2>/dev/null; getprop sys.boot.reason 2>/dev/null", cancellationToken)
                    .ConfigureAwait(false);
                if (state.Contains("recovery", StringComparison.OrdinalIgnoreCase))
                    recoveryMode = DeviceConnectionMode.Recovery;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[Rescue] Recovery durumu okunamadı");
            }
        }

        var stableId = _adb.SelectedDevice is not null
            ? _identity.ComputeStableId(null, _adb.SelectedDevice.Serial, null, null)
            : fastbootDevices.FirstOrDefault();

        var dbBackups = await _platform.ListPartitionBackupsAsync(stableId, cancellationToken).ConfigureAwait(false);
        var fileBackups = ScanLocalBackups(stableId);
        var all = dbBackups.Concat(fileBackups)
            .GroupBy(b => b.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(b => b.CreatedAt)
            .ToList();

        var actions = new List<string>();
        if (adbMode == DeviceConnectionMode.Adb)
        {
            actions.Add("Recovery'ye yeniden başlat");
            actions.Add("Bootloader'a yeniden başlat");
            if (all.Count > 0)
                actions.Add("Mevcut yedekleri incele");
        }
        else if (fastbootMode == DeviceConnectionMode.Fastboot)
        {
            actions.Add("Sisteme yeniden başlat (fastboot reboot)");
            if (all.Count > 0)
                actions.Add("Partition yedeğini doğrula — kör flash yapma");
            actions.Add("Vendor-specific kurtarma gerekebilir (EDL/BROM desteklenmez)");
        }
        else
        {
            actions.Add("USB kablosu ve sürücüleri kontrol et");
            actions.Add("Cihazı Fastboot veya Recovery'ye almayı dene");
        }

        var summary = BuildSummary(adbMode, fastbootMode, fastbootDevices.Count, all.Count);

        return new RescueSnapshot
        {
            Adb = adbMode,
            Fastboot = fastbootMode,
            Recovery = recoveryMode,
            AvailableBackups = all,
            Summary = summary,
            SafeActions = actions
        };
    }

    public Task<IReadOnlyList<string>> GetFastbootDevicesAsync(CancellationToken cancellationToken = default) =>
        FastbootDeviceProbe.GetFastbootDevicesAsync(cancellationToken);

    public Task<bool> IsDeviceInFastbootAsync(string serial, CancellationToken cancellationToken = default) =>
        FastbootDeviceProbe.IsDeviceInFastbootAsync(serial, cancellationToken);

    public Task<IReadOnlyList<PartitionBackupRecord>> ListPartitionBackupsAsync(string? stableDeviceId = null, CancellationToken cancellationToken = default) =>
        _platform.ListPartitionBackupsAsync(stableDeviceId, cancellationToken);

    public Task<DeviceToolResult> RebootSystemAsync(CancellationToken cancellationToken = default) =>
        _tools.RebootAsync(DeviceRebootMode.System, cancellationToken);

    public Task<DeviceToolResult> RebootFastbootAsync(CancellationToken cancellationToken = default) =>
        _tools.RebootAsync(DeviceRebootMode.Bootloader, cancellationToken);

    private static string BuildSummary(
        DeviceConnectionMode adb,
        DeviceConnectionMode fastboot,
        int fastbootCount,
        int backupCount)
    {
        if (adb == DeviceConnectionMode.Adb)
            return backupCount > 0
                ? $"ADB aktif · {backupCount} partition yedeği"
                : "ADB aktif · partition yedeği yok";

        if (fastboot == DeviceConnectionMode.Fastboot)
            return backupCount > 0
                ? $"Fastboot aktif ({fastbootCount} cihaz) · {backupCount} yedek — kör flash yapmayın"
                : $"Fastboot aktif ({fastbootCount} cihaz) · partition yedeği yok";

        return "Cihaz ADB/Fastboot'ta görünmüyor";
    }

    private static IReadOnlyList<PartitionBackupRecord> ScanLocalBackups(string? stableDeviceId)
    {
        if (!Directory.Exists(RootBackupDir))
            return [];

        return Directory.EnumerateFiles(RootBackupDir, "*.img", SearchOption.AllDirectories)
            .Select(file =>
            {
                var info = new FileInfo(file);
                return new PartitionBackupRecord
                {
                    PartitionName = Path.GetFileNameWithoutExtension(file),
                    FilePath = file,
                    SizeBytes = info.Length,
                    CreatedAt = info.LastWriteTimeUtc,
                    StableDeviceId = stableDeviceId ?? "",
                    Source = "local"
                };
            })
            .ToList();
    }
}
