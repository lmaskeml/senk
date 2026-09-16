using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Security.Root;
using Serilog;

namespace AndroidManager.Security.Services;

/// <summary>
/// Root temizleme — önce karantina (mv), sonra doğrulama. Direkt rm yok.
/// </summary>
public sealed class RootCleaner
{
    public const string QuarantineDir = "/data/local/tmp/androidmanager_quarantine";

    private readonly IAdbService _adb;
    private readonly RootManager _root;
    private readonly ILogger _logger;

    private RootShell Shell => _root.Shell;

    public RootCleaner(IAdbService adb, RootManager root, ILogger? logger = null)
    {
        _adb = adb;
        _root = root;
        _logger = logger ?? Log.ForContext<RootCleaner>();
    }

    public async Task<bool> SetSystemMountAsync(bool writable, CancellationToken cancellationToken = default)
    {
        var mode = writable ? "rw" : "ro";
        string[] targets = ["/system", "/", "/vendor", "/product", "/system_ext"];

        foreach (var target in targets)
        {
            try
            {
                await Shell.RunRootCommandAsync($"mount -o {mode},remount {target}", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // hedef yok olabilir
            }
        }

        if (writable)
        {
            var test = await Shell.RunRootCommandAsync(
                "touch /system/.am_rw_test 2>/dev/null && " +
                "rm -f /system/.am_rw_test && echo RW_OK || echo RW_FAIL",
                cancellationToken).ConfigureAwait(false);
            return test.Contains("RW_OK", StringComparison.Ordinal);
        }

        var mounts = await ReadMountFlagsAsync(cancellationToken).ConfigureAwait(false);
        return !mounts.Any(m =>
            (m.Path is "/system" or "/" or "/vendor" or "/product") && !m.ReadOnly);
    }

    public async Task<CleanResult> RemoveSystemAppAsync(ThreatItem threat, CancellationToken cancellationToken)
    {
        var pkg = threat.PackageName;
        if (string.IsNullOrWhiteSpace(pkg))
        {
            return new CleanResult
            {
                Threat = threat,
                Success = false,
                ActionTaken = CleanAction.ManualRequired,
                Message = "Paket adı yok"
            };
        }

        try
        {
            await Shell.RunRootCommandAsync($"pm uninstall -k --user 0 {pkg}", cancellationToken)
                .ConfigureAwait(false);
            await _adb.ExecuteShellAsync($"pm uninstall -k --user 0 {pkg}", cancellationToken)
                .ConfigureAwait(false);

            var pmPath = (await _adb.ExecuteShellAsync($"pm path {pkg} 2>/dev/null", cancellationToken)
                .ConfigureAwait(false)).Trim();
            var apkPaths = pmPath
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Replace("package:", "", StringComparison.Ordinal).Trim())
                .Where(p => p.StartsWith('/'))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (apkPaths.Count == 0)
            {
                var gone = await IsPackageGoneAsync(pkg, cancellationToken).ConfigureAwait(false);
                return new CleanResult
                {
                    Threat = threat,
                    Success = gone,
                    ActionTaken = CleanAction.Disabled,
                    Message = gone
                        ? $"{pkg} kaldırıldı / devre dışı"
                        : $"{pkg} bulunamadı — manuel inceleme gerekli"
                };
            }

            var rw = await SetSystemMountAsync(true, cancellationToken).ConfigureAwait(false);
            if (!rw)
            {
                return new CleanResult
                {
                    Threat = threat,
                    Success = false,
                    ActionTaken = CleanAction.ManualRequired,
                    Message =
                        "Sistem bölümü salt-okunur — APK silinemedi. " +
                        "Uygulama en azından user 0 için devre dışı bırakıldı. " +
                        "Magisk ile rw mount gerekebilir."
                };
            }

            var moved = new List<string>();
            await Shell.RunRootCommandAsync($"mkdir -p {QuarantineDir}/apps", cancellationToken)
                .ConfigureAwait(false);

            foreach (var apk in apkPaths)
            {
                var slash = apk.LastIndexOf('/');
                if (slash <= 0) continue;
                var dir = apk[..slash];
                var dest = $"{QuarantineDir}/apps/{LastSegment(dir)}_{DateTime.Now:yyyyMMddHHmmss}";

                var mv = await Shell.RunRootCommandAsync(
                    $"mv \"{dir}\" \"{dest}\" 2>/dev/null && echo OK || echo FAIL",
                    cancellationToken).ConfigureAwait(false);
                if (!mv.Contains("OK", StringComparison.Ordinal))
                {
                    await Shell.RunRootCommandAsync(
                        $"cp -r \"{dir}\" \"{dest}\" 2>/dev/null && rm -rf \"{dir}\" 2>/dev/null",
                        cancellationToken).ConfigureAwait(false);
                }

                moved.Add(dest);
            }

            await Shell.RunRootCommandAsync($"pm uninstall {pkg}", cancellationToken).ConfigureAwait(false);
            var gone2 = await IsPackageGoneAsync(pkg, cancellationToken).ConfigureAwait(false);

            return new CleanResult
            {
                Threat = threat,
                Success = gone2,
                ActionTaken = CleanAction.Quarantined,
                Message = gone2
                    ? $"{pkg} kaldırıldı — APK karantinada: {string.Join(", ", moved)}"
                    : "APK karantinada ama paket kaydı duruyor — yeniden başlatma gerekebilir"
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Sistem uygulaması kaldırma hatası: {Pkg}", pkg);
            return new CleanResult
            {
                Threat = threat,
                Success = false,
                ActionTaken = CleanAction.ManualRequired,
                Message = $"Hata: {ex.Message}"
            };
        }
    }

    public async Task<CleanResult> QuarantineSystemFileAsync(ThreatItem threat, CancellationToken cancellationToken)
    {
        var src = threat.FilePath;
        if (string.IsNullOrWhiteSpace(src))
        {
            return new CleanResult
            {
                Threat = threat,
                Success = false,
                ActionTaken = CleanAction.ManualRequired,
                Message = "Dosya yolu yok"
            };
        }

        try
        {
            var dest = $"{QuarantineDir}/files/{LastSegment(src)}_{DateTime.Now:yyyyMMddHHmmss}";
            await Shell.RunRootCommandAsync($"mkdir -p {QuarantineDir}/files", cancellationToken)
                .ConfigureAwait(false);

            var mv = await Shell.RunRootCommandAsync(
                $"mv \"{src}\" \"{dest}\" 2>/dev/null && echo OK || echo FAIL",
                cancellationToken).ConfigureAwait(false);

            if (!mv.Contains("OK", StringComparison.Ordinal))
            {
                var cp = await Shell.RunRootCommandAsync(
                    $"cp -r \"{src}\" \"{dest}\" 2>/dev/null && rm -rf \"{src}\" 2>/dev/null && echo OK || echo FAIL",
                    cancellationToken).ConfigureAwait(false);
                if (!cp.Contains("OK", StringComparison.Ordinal))
                {
                    return new CleanResult
                    {
                        Threat = threat,
                        Success = false,
                        ActionTaken = CleanAction.ManualRequired,
                        Message = $"Taşınamadı: {src} (salt-okunur bölüm?)"
                    };
                }
            }

            var check = await Shell.RunRootCommandAsync(
                $"ls \"{src}\" 2>/dev/null || echo GONE", cancellationToken).ConfigureAwait(false);
            var gone = check.Contains("GONE", StringComparison.Ordinal);

            return new CleanResult
            {
                Threat = threat,
                Success = gone,
                ActionTaken = CleanAction.Quarantined,
                Message = gone
                    ? $"Karantinaya alındı: {dest}"
                    : "Kısmen taşındı — orijinal silinemedi"
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Karantina hatası: {Path}", src);
            return new CleanResult
            {
                Threat = threat,
                Success = false,
                ActionTaken = CleanAction.ManualRequired,
                Message = $"Hata: {ex.Message}"
            };
        }
    }

    public async Task<CleanResult> RestoreHostsAsync(CancellationToken cancellationToken)
    {
        var dummy = new ThreatItem
        {
            Name = "Hosts Dosyası",
            Type = ThreatType.Malware,
            Severity = ThreatSeverity.High,
            Category = ThreatCategory.Network
        };

        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            await Shell.RunRootCommandAsync(
                $"mkdir -p {QuarantineDir}/backup && " +
                $"cp /etc/hosts {QuarantineDir}/backup/hosts_{stamp} 2>/dev/null",
                cancellationToken).ConfigureAwait(false);

            await SetSystemMountAsync(true, cancellationToken).ConfigureAwait(false);

            var write = await Shell.RunRootCommandAsync(
                "printf '127.0.0.1 localhost\\n::1 ip6-localhost\\n' > /etc/hosts && " +
                "chmod 644 /etc/hosts && chown root:root /etc/hosts && " +
                "chcon u:object_r:hosts_file:s0 /etc/hosts 2>/dev/null; cat /etc/hosts",
                cancellationToken).ConfigureAwait(false);

            var ok = write.Contains("localhost", StringComparison.Ordinal) &&
                     !write.Contains("google", StringComparison.OrdinalIgnoreCase);

            if (ok)
                await SetSystemMountAsync(false, cancellationToken).ConfigureAwait(false);

            return new CleanResult
            {
                Threat = dummy,
                Success = ok,
                ActionTaken = CleanAction.FileDeleted,
                Message = ok
                    ? $"hosts varsayılana döndürüldü (yedek: hosts_{stamp})"
                    : "hosts onarılamadı"
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "hosts onarma hatası");
            return new CleanResult
            {
                Threat = dummy,
                Success = false,
                ActionTaken = CleanAction.ManualRequired,
                Message = $"Hata: {ex.Message}"
            };
        }
    }

    public async Task<bool> KillProcessAsync(string packageName, CancellationToken cancellationToken)
    {
        try
        {
            await Shell.RunRootCommandAsync($"am force-stop {packageName} 2>/dev/null", cancellationToken)
                .ConfigureAwait(false);
            var pids = await Shell.RunRootCommandAsync($"pidof {packageName} 2>/dev/null", cancellationToken)
                .ConfigureAwait(false);
            var killed = false;
            foreach (var pid in pids.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(pid, out _)) continue;
                await Shell.RunRootCommandAsync($"kill -9 {pid} 2>/dev/null", cancellationToken)
                    .ConfigureAwait(false);
                killed = true;
            }

            return killed;
        }
        catch
        {
            return false;
        }
    }

    public async Task<CleanResult> ClearAppDataAsync(string packageName, CancellationToken cancellationToken)
    {
        var threat = new ThreatItem { Name = packageName, PackageName = packageName };
        try
        {
            var result = await Shell.RunRootCommandAsync($"pm clear {packageName}", cancellationToken)
                .ConfigureAwait(false);
            var ok = result.Contains("Success", StringComparison.OrdinalIgnoreCase);
            return new CleanResult
            {
                Threat = threat,
                Success = ok,
                ActionTaken = CleanAction.FileDeleted,
                Message = ok
                    ? $"{packageName} verileri temizlendi"
                    : $"Veri temizlenemedi: {result.Trim()}"
            };
        }
        catch (Exception ex)
        {
            return new CleanResult
            {
                Threat = threat,
                Success = false,
                ActionTaken = CleanAction.ManualRequired,
                Message = $"Hata: {ex.Message}"
            };
        }
    }

    public async Task<bool> RebootAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Shell.RunRootCommandAsync("reboot", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // bağlantı kopması beklenir
        }

        return true;
    }

    private async Task<bool> IsPackageGoneAsync(string pkg, CancellationToken ct)
    {
        var check = (await _adb.ExecuteShellAsync(
            $"pm list packages | grep -x 'package:{pkg}' || echo GONE", ct).ConfigureAwait(false)).Trim();
        return check == "GONE" || !check.Contains(pkg, StringComparison.Ordinal);
    }

    private async Task<List<(string Path, bool ReadOnly)>> ReadMountFlagsAsync(CancellationToken ct)
    {
        var list = new List<(string, bool)>();
        try
        {
            var raw = await _adb.ExecuteShellAsync("cat /proc/mounts 2>/dev/null", ct).ConfigureAwait(false);
            foreach (var line in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) continue;
                list.Add((parts[1],
                    parts[3].Contains("ro", StringComparison.Ordinal) &&
                    !parts[3].Contains("rw", StringComparison.Ordinal)));
            }
        }
        catch
        {
            // ignore
        }

        return list;
    }

    private static string LastSegment(string path)
    {
        var i = path.LastIndexOf('/');
        return i >= 0 ? path[(i + 1)..] : path;
    }
}
