using System.Text.Json;
using System.Text.Json.Serialization;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Security.Root;
using Serilog;

namespace AndroidManager.Security.Services;

public sealed class RomBackupVaultService : IRomBackupVaultService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IAdbService _adb;
    private readonly IRecoveryManagerService _recovery;
    private readonly IRootAnalysisService _analysis;
    private readonly RootManager _root;
    private readonly IPlatformRepository _platform;
    private readonly IDeviceIdentityService _identity;
    private readonly ISettingsService _settings;
    private readonly ILogger _logger;

    public RomBackupVaultService(
        IAdbService adb,
        IRecoveryManagerService recovery,
        IRootAnalysisService analysis,
        RootManager root,
        IPlatformRepository platform,
        IDeviceIdentityService identity,
        ISettingsService settings,
        ILogger? logger = null)
    {
        _adb = adb;
        _recovery = recovery;
        _analysis = analysis;
        _root = root;
        _platform = platform;
        _identity = identity;
        _settings = settings;
        _logger = logger ?? Log.ForContext<RomBackupVaultService>();
    }

    public async Task<RomBackupCapability> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var serial = _adb.SelectedDevice?.Serial ?? "";
        var fastboot = await FastbootDeviceProbe.GetFastbootDevicesAsync(cancellationToken).ConfigureAwait(false);

        string codename = "";
        string model = "";
        string slot = "";
        DeviceConnectionMode recoveryMode = DeviceConnectionMode.Unknown;
        try
        {
            if (_adb.SelectedDevice is not null)
            {
                var profile = await _analysis.AnalyzeDeviceAsync(cancellationToken).ConfigureAwait(false);
                codename = profile.Codename;
                model = profile.DisplayName;
                slot = profile.ActiveSlot;
                var status = await _recovery.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                recoveryMode = status.ConnectionMode;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[RomDump] profile probe failed");
        }

        if (recoveryMode == DeviceConnectionMode.Recovery)
        {
            return new RomBackupCapability
            {
                Method = RomBackupMethod.TwrpDd,
                FastbootAvailable = fastboot.Count > 0,
                Codename = codename,
                Serial = serial,
                Model = model,
                Slot = slot,
                Detail = "TWRP/recovery algılandı — ham dd dökümü (exec-out) kullanılacak."
            };
        }

        if (_adb.SelectedDevice is not null)
        {
            var rooted = await _root.EnsureActiveAsync(cancellationToken).ConfigureAwait(false);
            if (rooted)
            {
                return new RomBackupCapability
                {
                    Method = RomBackupMethod.LiveRoot,
                    FastbootAvailable = fastboot.Count > 0,
                    Codename = codename,
                    Serial = serial,
                    Model = model,
                    Slot = slot,
                    Detail = "Cihaz açık ve root doğrulandı — canlı dd dökümü kullanılacak."
                };
            }
        }

        return new RomBackupCapability
        {
            Method = RomBackupMethod.None,
            FastbootAvailable = fastboot.Count > 0,
            Codename = codename,
            Serial = serial,
            Model = model,
            Slot = slot,
            Detail = fastboot.Count > 0
                ? "Yedek almak için TWRP veya root gerekir. Fastboot restore hazır."
                : "TWRP'ye girin veya Magisk root izni verin. Fastboot cihazı yok."
        };
    }

    public async Task<IReadOnlyList<DevicePartitionInfo>> ListPartitionsAsync(
        CancellationToken cancellationToken = default)
    {
        var capability = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!capability.CanBackup)
            return [];

        var listing = await RunPrivilegedAsync("ls -al /dev/block/by-name", capability.Method, cancellationToken)
            .ConfigureAwait(false);
        var proc = await RunPrivilegedAsync("cat /proc/partitions", capability.Method, cancellationToken)
            .ConfigureAwait(false);

        return RomPartitionCatalog.BuildMap(listing, proc);
    }

    public async Task<RomBackupSession> CreateBackupAsync(
        RomBackupRequest request,
        IProgress<RomBackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var capability = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!capability.CanBackup)
            throw new InvalidOperationException(capability.Detail);

        var available = await ListPartitionsAsync(cancellationToken).ConfigureAwait(false);
        var selected = available
            .Where(p => request.PartitionNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
            .Where(p => !p.IsSlotAlias)
            .Where(p => !RomPartitionCatalog.IsForbidden(p.Name))
            .ToList();

        if (selected.Count == 0)
            throw new InvalidOperationException("Yedeklenecek bölüm seçilmedi veya cihazda yok.");

        var rootDir = string.IsNullOrWhiteSpace(request.OutputDirectory)
            ? DefaultDumpRoot()
            : request.OutputDirectory;
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var safeCode = string.IsNullOrWhiteSpace(capability.Codename) ? "device" : capability.Codename;
        var folder = Path.Combine(rootDir, $"{safeCode}_{stamp}");
        Directory.CreateDirectory(folder);

        var files = new List<RomBackupPartitionFile>();
        var errors = new List<string>();
        var totalBytes = selected.Sum(p => Math.Max(p.SizeBytes, 0));
        long doneBytes = 0;

        for (var i = 0; i < selected.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var part = selected[i];
            var local = Path.Combine(folder, part.Name + ".img");
            Report(progress, part.Name, i + 1, selected.Count, doneBytes, totalBytes,
                $"{part.Name} dökülüyor ({part.SizeFormatted})…");

            var command = await BuildDdCommandAsync(part.BlockPath, capability.Method, cancellationToken)
                .ConfigureAwait(false);
            var dump = await _adb.ExecOutToFileAsync(
                    command,
                    local,
                    part.SizeBytes,
                    new Progress<TransferProgress>(p =>
                    {
                        var overall = totalBytes > 0
                            ? doneBytes + p.BytesTransferred
                            : 0;
                        Report(progress, part.Name, i + 1, selected.Count, overall, totalBytes,
                            $"{part.Name}: {p.Percentage}%");
                    }),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!dump.Success)
            {
                errors.Add($"{part.Name}: {dump.Message}");
                _logger.Warning("[RomDump] {Partition} failed: {Message}", part.Name, dump.Message);
                continue;
            }

            doneBytes += dump.BytesWritten;
            var record = new RomBackupPartitionFile
            {
                Name = part.Name,
                FileName = Path.GetFileName(local),
                SizeBytes = dump.BytesWritten,
                Sha256 = dump.Sha256,
                Category = part.Category
            };
            files.Add(record);

            var stableId = _identity.ComputeStableId(null, capability.Serial, null, capability.Model);
            await _platform.SavePartitionBackupAsync(new PartitionBackupRecord
            {
                StableDeviceId = stableId,
                DeviceSerial = capability.Serial,
                DeviceModel = capability.Model,
                PartitionName = part.Name,
                FilePath = local,
                Sha256 = dump.Sha256,
                SizeBytes = dump.BytesWritten,
                CreatedAt = DateTime.UtcNow,
                Source = capability.Method.ToString()
            }, cancellationToken).ConfigureAwait(false);
        }

        var session = new RomBackupSession
        {
            Folder = folder,
            Codename = capability.Codename,
            Serial = capability.Serial,
            Model = capability.Model,
            Slot = capability.Slot,
            Method = capability.Method,
            CreatedAt = DateTime.Now,
            Partitions = files,
            Error = errors.Count == 0 ? null : string.Join("; ", errors)
        };

        var manifestPath = Path.Combine(folder, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(session, JsonOpts), cancellationToken)
            .ConfigureAwait(false);

        if (files.Count == 0)
            throw new InvalidOperationException(session.Error ?? "Hiçbir bölüm yedeklenemedi.");

        Report(progress, "", selected.Count, selected.Count, doneBytes, totalBytes, "Yedekleme tamamlandı.");
        return session;
    }

    public async Task<IReadOnlyList<RomBackupSession>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        var roots = new[] { DefaultDumpRoot(), Path.Combine(_settings.Current.DefaultBackupPath, "RomDump") }
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var sessions = new List<RomBackupSession>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
                continue;

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var loaded = await LoadSessionAsync(dir, cancellationToken).ConfigureAwait(false);
                if (loaded is not null)
                    sessions.Add(loaded);
            }
        }

        return sessions.OrderByDescending(s => s.CreatedAt).ToList();
    }

    public async Task<RomBackupSession?> LoadSessionAsync(string folder, CancellationToken cancellationToken = default)
    {
        var manifest = Path.Combine(folder, "manifest.json");
        if (!File.Exists(manifest))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(manifest, cancellationToken).ConfigureAwait(false);
            var session = JsonSerializer.Deserialize<RomBackupSession>(json, JsonOpts);
            if (session is null)
                return null;
            return new RomBackupSession
            {
                Version = session.Version,
                Folder = folder,
                Codename = session.Codename,
                Serial = session.Serial,
                Model = session.Model,
                Slot = session.Slot,
                Method = session.Method,
                CreatedAt = session.CreatedAt,
                Partitions = session.Partitions,
                Error = session.Error
            };
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[RomDump] manifest okunamadı: {Folder}", folder);
            return null;
        }
    }

    public async Task<DeviceToolResult> RestoreAsync(
        RomBackupRestoreRequest request,
        IProgress<RomBackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var session = await LoadSessionAsync(request.Folder, cancellationToken).ConfigureAwait(false);
        if (session is null)
            return new DeviceToolResult { Success = false, Message = "Yedek oturumu bulunamadı." };

        var devices = await FastbootDeviceProbe.GetFastbootDevicesAsync(cancellationToken).ConfigureAwait(false);
        if (devices.Count == 0)
            return new DeviceToolResult { Success = false, Message = "Fastboot cihazı yok. Bootloader'a alın." };

        var serial = request.FastbootSerial;
        if (string.IsNullOrWhiteSpace(serial))
        {
            serial = devices.FirstOrDefault(d =>
                         d.Equals(session.Serial, StringComparison.OrdinalIgnoreCase))
                     ?? devices[0];
        }

        var wanted = session.Partitions
            .Where(p => request.PartitionNames.Count == 0
                        || request.PartitionNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
            .Where(p => !RomPartitionCatalog.IsForbidden(p.Name))
            .ToList();

        if (wanted.Count == 0)
            return new DeviceToolResult { Success = false, Message = "Geri yüklenecek imaj yok." };

        var failures = new List<string>();
        for (var i = 0; i < wanted.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var part = wanted[i];
            var path = Path.Combine(session.Folder, part.FileName);
            if (!File.Exists(path))
            {
                failures.Add($"{part.Name}: dosya yok ({part.FileName})");
                continue;
            }

            var fileBytes = new FileInfo(path).Length;
            if (FastbootPartitionHelper.IsBootOrRecoveryStem(part.Name)
                && part.SizeBytes > 0
                && fileBytes > part.SizeBytes)
            {
                failures.Add(
                    FastbootPartitionHelper.ExplainFlashFailure(part.Name,
                        "Yedek dosyası kayıtlı bölüm boyutundan büyük — flash atlandı.",
                        fileBytes, part.SizeBytes));
                continue;
            }

            Report(progress, part.Name, i + 1, wanted.Count, i, wanted.Count,
                $"fastboot flash {FastbootPartitionHelper.ResolveFlashTarget(part.Name)}…");

            var result = await FastbootDeviceProbe.FlashPartitionAsync(serial, part.Name, path, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                failures.Add(FastbootPartitionHelper.ExplainFlashFailure(
                    part.Name, result.Message, fileBytes, part.SizeBytes));
            }
        }

        if (failures.Count > 0)
        {
            return new DeviceToolResult
            {
                Success = false,
                Message = string.Join("\n", failures)
            };
        }

        Report(progress, "", wanted.Count, wanted.Count, wanted.Count, wanted.Count, "Restore tamamlandı.");
        return new DeviceToolResult
        {
            Success = true,
            Message = $"{wanted.Count} bölüm flash edildi ({serial})."
        };
    }

    private async Task<string> RunPrivilegedAsync(
        string command,
        RomBackupMethod method,
        CancellationToken cancellationToken)
    {
        if (method == RomBackupMethod.LiveRoot)
            return await _root.Shell.RunRootCommandAsync(command, TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);

        return await _adb.ExecuteShellAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> BuildDdCommandAsync(
        string blockPath,
        RomBackupMethod method,
        CancellationToken cancellationToken)
    {
        var dd = $"dd if={blockPath} bs=1048576";
        if (method == RomBackupMethod.TwrpDd)
            return dd;
        if (await _root.Shell.IsAdbShellRootAsync(cancellationToken).ConfigureAwait(false))
            return dd;
        return $"su -c '{dd}'";
    }

    private string DefaultDumpRoot()
    {
        var path = Path.Combine(_settings.Current.DefaultBackupPath, "RomDump");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Report(
        IProgress<RomBackupProgress>? progress,
        string partition,
        int index,
        int total,
        long transferred,
        long overall,
        string message)
    {
        var percent = overall > 0
            ? (int)Math.Clamp(transferred * 100 / overall, 0, 100)
            : (total <= 0 ? 0 : index * 100 / total);

        progress?.Report(new RomBackupProgress
        {
            PartitionName = partition,
            CurrentIndex = index,
            TotalCount = total,
            Percent = percent,
            BytesTransferred = transferred,
            TotalBytes = overall,
            Message = message
        });
    }
}
