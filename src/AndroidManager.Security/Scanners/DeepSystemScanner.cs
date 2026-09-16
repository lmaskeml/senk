using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Security.Root;
using Serilog;

namespace AndroidManager.Security.Scanners;

/// <summary>Root destekli derin sistem tarama motoru (Sprint 8.5).</summary>
public sealed class DeepSystemScanner
{
    private static readonly (string Path, string Name, string Family)[] KnownRootkitPaths =
    [
        ("/system/bin/zk", "zk", "DroidDream"),
        ("/system/bin/rageagainstthecage", "rageagainstthecage", "DroidDream"),
        ("/system/bin/exploid", "exploid", "DroidDream"),
        ("/system/xbin/iku", "iku", "GingerMaster"),
        ("/system/xbin/ku", "ku", "GingerMaster"),
        ("/system/usr/weird", "weird", "GingerMaster"),
        ("/system/bin/ixpdata", "ixpdata", "Geinimi"),
        ("/data/local/tmp/gear", "gear", "GhostPush"),
        ("/system/bin/loc", "loc", "Xavier"),
        ("/system/bin/.su", ".su", "HiddenSu"),
        ("/system/xbin/.su", ".su", "HiddenSu"),
        ("/vendor/bin/su", "su", "HiddenSu")
    ];

    private static readonly string[] TrustedPrefixes =
    [
        "com.android.", "com.google.", "com.samsung.", "com.sec.", "com.xiaomi.",
        "com.miui.", "com.huawei.", "com.oppo.", "com.oneplus.", "com.realme.",
        "com.vivo.", "com.coloros.", "android."
    ];

    private readonly IAdbService _adb;
    private readonly RootManager _root;
    private readonly ILogger _logger;

    public DeepSystemScanner(IAdbService adb, RootManager root, ILogger? logger = null)
    {
        _adb = adb;
        _root = root;
        _logger = logger ?? Log.ForContext<DeepSystemScanner>();
    }

    public async Task<SystemScanResult> ScanAsync(
        SystemScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var threats = new List<ThreatItem>();
        var checks = 0;

        Report(progress, "Root doğrulama", 2);
        var rooted = await _root.EnsureActiveAsync(cancellationToken).ConfigureAwait(false);
        if (!rooted)
        {
            return new SystemScanResult
            {
                RootAvailable = false,
                Duration = sw.Elapsed,
                Threats = [],
                Boot = await GetBootSecurityAsync(cancellationToken).ConfigureAwait(false),
                Mounts = await GetMountStatusAsync(cancellationToken).ConfigureAwait(false)
            };
        }

        Report(progress, "Bölüm / boot / SELinux", 8);
        var mounts = await GetMountStatusAsync(cancellationToken).ConfigureAwait(false);
        var boot = await GetBootSecurityAsync(cancellationToken).ConfigureAwait(false);
        checks += 3;
        threats.AddRange(AssessBootAndMount(boot, mounts));

        Report(progress, "Bilinen rootkit dosyaları", 18);
        threats.AddRange(await ScanKnownRootkitsAsync(cancellationToken).ConfigureAwait(false));
        checks++;

        Report(progress, "Gizli su binary", 28);
        threats.AddRange(await ScanHiddenSuAsync(cancellationToken).ConfigureAwait(false));
        checks++;

        if (options.ScanRecentFiles)
        {
            Report(progress, "Değişen sistem dosyaları", 40);
            threats.AddRange(await ScanRecentSystemFilesAsync(cancellationToken).ConfigureAwait(false));
            checks++;
        }

        Report(progress, "Dünya yazılabilir dosyalar", 50);
        threats.AddRange(await ScanWorldWritableAsync(cancellationToken).ConfigureAwait(false));
        checks++;

        if (options.ScanInitScripts)
        {
            Report(progress, "Init scriptleri", 60);
            threats.AddRange(await ScanInitScriptsAsync(cancellationToken).ConfigureAwait(false));
            checks++;
        }

        if (options.ScanMagiskModules)
        {
            Report(progress, "Magisk modülleri", 70);
            threats.AddRange(await ScanMagiskModulesAsync(cancellationToken).ConfigureAwait(false));
            checks++;
        }

        if (options.ScanAccessibility)
        {
            Report(progress, "Erişilebilirlik / cihaz yöneticisi", 80);
            threats.AddRange(await ScanAccessibilityAndAdminsAsync(cancellationToken).ConfigureAwait(false));
            checks++;
        }

        if (options.ScanHosts)
        {
            Report(progress, "hosts dosyası", 88);
            threats.AddRange(await ScanHostsAsync(cancellationToken).ConfigureAwait(false));
            checks++;
        }

        if (options.ScanDataLocalTmp)
        {
            Report(progress, "/data/local/tmp", 95);
            threats.AddRange(await ScanDataLocalTmpAsync(cancellationToken).ConfigureAwait(false));
            checks++;
        }

        Report(progress, "Tamamlandı", 100, threats.Count);
        sw.Stop();

        return new SystemScanResult
        {
            Threats = threats
                .GroupBy(t => $"{t.Category}|{t.PackageName}|{t.FilePath}|{t.Name}", StringComparer.Ordinal)
                .Select(g => g.OrderByDescending(t => t.Severity).First())
                .OrderByDescending(t => t.Severity)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Boot = boot,
            Mounts = mounts,
            ScannedChecks = checks,
            Duration = sw.Elapsed,
            RootAvailable = true
        };
    }

    public async Task<MountStatus> GetMountStatusAsync(CancellationToken cancellationToken)
    {
        var mounts = new List<MountInfo>();
        try
        {
            var raw = await _adb.ExecuteShellAsync("cat /proc/mounts 2>/dev/null", cancellationToken)
                .ConfigureAwait(false);
            foreach (var line in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) continue;
                var opts = parts[3];
                mounts.Add(new MountInfo
                {
                    Device = parts[0],
                    Path = parts[1],
                    FileSystem = parts[2],
                    IsReadOnly = opts.Contains("ro", StringComparison.Ordinal) &&
                                 !opts.Contains("rw", StringComparison.Ordinal)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "mount okuma hatası");
        }

        return new MountStatus { Mounts = mounts };
    }

    public async Task<BootSecurityInfo> GetBootSecurityAsync(CancellationToken cancellationToken)
    {
        async Task<string> Prop(string name) =>
            (await _adb.ExecuteShellAsync($"getprop {name}", cancellationToken).ConfigureAwait(false)).Trim();

        try
        {
            var vb = await Prop("ro.boot.verifiedbootstate").ConfigureAwait(false);
            var locked = await Prop("ro.boot.flash.locked").ConfigureAwait(false);
            var debuggable = await Prop("ro.debuggable").ConfigureAwait(false);
            var secure = await Prop("ro.secure").ConfigureAwait(false);
            var oem = await Prop("sys.oem_unlock_allowed").ConfigureAwait(false);
            var sdk = await Prop("ro.build.version.sdk").ConfigureAwait(false);
            var selinux = (await _adb.ExecuteShellAsync("getenforce 2>/dev/null", cancellationToken)
                .ConfigureAwait(false)).Trim();

            return new BootSecurityInfo
            {
                VerifiedBootState = string.IsNullOrWhiteSpace(vb) ? "bilinmiyor" : vb,
                BootloaderLocked = locked is not "0",
                SelinuxMode = string.IsNullOrWhiteSpace(selinux) ? "bilinmiyor" : selinux,
                Debuggable = debuggable is "1",
                SecureBuild = secure is not "0",
                OemUnlockAllowed = oem is "1",
                AndroidSdk = sdk
            };
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "boot security query failed");
            return new BootSecurityInfo();
        }
    }

    private static List<ThreatItem> AssessBootAndMount(BootSecurityInfo boot, MountStatus mounts)
    {
        var list = new List<ThreatItem>();
        if (boot.VerifiedBootState is "orange" or "red")
        {
            list.Add(Threat(
                $"Verified boot: {boot.VerifiedBootState}",
                ThreatType.Suspicious,
                ThreatSeverity.High,
                ThreatCategory.Integrity,
                "Boot zinciri doğrulanamadı — stok ROM / yeniden flash değerlendirin.",
                score: 20));
        }

        if (!boot.SelinuxEnforcing)
        {
            list.Add(Threat(
                $"SELinux: {boot.SelinuxMode}",
                ThreatType.RiskFactor,
                ThreatSeverity.High,
                ThreatCategory.Integrity,
                "SELinux zorlamıyor — malware için avantaj.",
                score: 18));
        }

        if (boot.Debuggable)
        {
            list.Add(Threat(
                "ro.debuggable=1",
                ThreatType.RiskFactor,
                ThreatSeverity.Medium,
                ThreatCategory.Integrity,
                "Debug build göstergesi.",
                score: 10));
        }

        if (mounts.SystemWritable)
        {
            list.Add(Threat(
                "Sistem bölümü rw",
                ThreatType.Suspicious,
                ThreatSeverity.High,
                ThreatCategory.Integrity,
                "Sistem yazılabilir — kalıcı rootkit riski.",
                score: 16));
        }

        return list;
    }

    private async Task<List<ThreatItem>> ScanKnownRootkitsAsync(CancellationToken ct)
    {
        var list = new List<ThreatItem>();
        var shell = _root.Shell;
        foreach (var (path, name, family) in KnownRootkitPaths)
        {
            var raw = await shell.RunRootCommandAsync(
                $"ls -la \"{path}\" 2>/dev/null || echo MISSING", ct).ConfigureAwait(false);
            if (raw.Contains("MISSING", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(raw))
                continue;

            list.Add(Threat(
                $"{family}: {name}",
                ThreatType.Rootkit,
                ThreatSeverity.Critical,
                ThreatCategory.System,
                $"Bilinen rootkit/kalıntı yolu: {path}",
                filePath: path,
                score: 40,
                confidence: 90));
        }

        return list;
    }

    private async Task<List<ThreatItem>> ScanHiddenSuAsync(CancellationToken ct)
    {
        var list = new List<ThreatItem>();
        var raw = await _root.Shell.RunRootCommandAsync(
            "ls -la /system/bin/.su /system/xbin/.su /vendor/bin/su /su/bin/su 2>/dev/null", ct)
            .ConfigureAwait(false);

        foreach (var line in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains("/su", StringComparison.OrdinalIgnoreCase))
                continue;
            var path = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
            if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
                continue;

            list.Add(Threat(
                $"Gizli su: {path}",
                ThreatType.Rootkit,
                ThreatSeverity.Critical,
                ThreatCategory.System,
                "Gizli veya vendor su binary.",
                filePath: path,
                score: 35,
                confidence: 85));
        }

        return list;
    }

    private async Task<List<ThreatItem>> ScanRecentSystemFilesAsync(CancellationToken ct)
    {
        var list = new List<ThreatItem>();
        try
        {
            var raw = await _root.Shell.RunRootCommandAsync(
                "find /system/app /system/priv-app /system/bin /system/xbin " +
                "-newer /system/build.prop -type f 2>/dev/null | head -n 40", ct)
                .ConfigureAwait(false);

            foreach (var path in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var p = path.Trim();
                if (!p.StartsWith('/')) continue;
                list.Add(Threat(
                    $"Kurulum sonrası değişen: {Last(p)}",
                    ThreatType.Suspicious,
                    ThreatSeverity.Medium,
                    ThreatCategory.System,
                    "OTA sonrası da değişebilir — doğrulayın.",
                    filePath: p,
                    score: 8,
                    confidence: 55));
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "recent files scan failed");
        }

        return list;
    }

    private async Task<List<ThreatItem>> ScanWorldWritableAsync(CancellationToken ct)
    {
        var list = new List<ThreatItem>();
        try
        {
            var raw = await _root.Shell.RunRootCommandAsync(
                "find /system -type f -perm -002 2>/dev/null | head -n 30", ct)
                .ConfigureAwait(false);
            foreach (var path in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var p = path.Trim();
                if (!p.StartsWith('/')) continue;
                list.Add(Threat(
                    $"Dünya yazılabilir: {Last(p)}",
                    ThreatType.Suspicious,
                    ThreatSeverity.High,
                    ThreatCategory.System,
                    "Her kullanıcı yazabilir.",
                    filePath: p,
                    score: 14,
                    confidence: 70));
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "world-writable scan failed");
        }

        return list;
    }

    private async Task<List<ThreatItem>> ScanInitScriptsAsync(CancellationToken ct)
    {
        var list = new List<ThreatItem>();
        try
        {
            var raw = await _root.Shell.RunRootCommandAsync(
                "grep -RInE 'chmod 777|mount -o rw|su -c|setenforce 0' " +
                "/system/etc/init /vendor/etc/init 2>/dev/null | head -n 40", ct)
                .ConfigureAwait(false);

            foreach (var line in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var file = line.Split(':')[0];
                list.Add(Threat(
                    $"Şüpheli init: {Last(file)}",
                    ThreatType.Suspicious,
                    ThreatSeverity.High,
                    ThreatCategory.Persistence,
                    line.Trim(),
                    filePath: file,
                    score: 16,
                    confidence: 65));
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "init scan failed");
        }

        return list;
    }

    private async Task<List<ThreatItem>> ScanMagiskModulesAsync(CancellationToken ct)
    {
        var list = new List<ThreatItem>();
        try
        {
            var mods = await _root.Shell.RunRootCommandAsync(
                "ls /data/adb/modules 2>/dev/null", ct).ConfigureAwait(false);
            foreach (var mod in mods.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var name = mod.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var bins = await _root.Shell.RunRootCommandAsync(
                    $"ls \"/data/adb/modules/{name}/system/bin\" " +
                    $"\"/data/adb/modules/{name}/system/xbin\" 2>/dev/null", ct)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(bins))
                    continue;

                list.Add(Threat(
                    $"Magisk modül binary: {name}",
                    ThreatType.Suspicious,
                    ThreatSeverity.Medium,
                    ThreatCategory.Persistence,
                    "Modül system/bin altına binary koyuyor — Magisk UI'dan doğrulayın.",
                    score: 10,
                    confidence: 60));
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "magisk module scan failed");
        }

        return list;
    }

    private async Task<List<ThreatItem>> ScanAccessibilityAndAdminsAsync(CancellationToken ct)
    {
        var list = new List<ThreatItem>();
        try
        {
            var a11y = await _adb.ExecuteShellAsync(
                "settings get secure enabled_accessibility_services 2>/dev/null", ct)
                .ConfigureAwait(false);
            foreach (Match m in Regex.Matches(a11y, @"([\w\.]+)/"))
            {
                var pkg = m.Groups[1].Value;
                if (IsTrusted(pkg)) continue;
                list.Add(Threat(
                    $"Erişilebilirlik: {pkg}",
                    ThreatType.Spyware,
                    ThreatSeverity.High,
                    ThreatCategory.Privilege,
                    "Bilinmeyen erişilebilirlik servisi.",
                    packageName: pkg,
                    score: 18,
                    confidence: 70));
            }

            var admins = await _adb.ExecuteShellAsync(
                "dumpsys device_policy 2>/dev/null | grep -E 'Admin|Package'", ct)
                .ConfigureAwait(false);
            foreach (Match m in Regex.Matches(admins, @"([\w\.]+)/[\w\.]+"))
            {
                var pkg = m.Groups[1].Value;
                if (IsTrusted(pkg)) continue;
                list.Add(Threat(
                    $"Cihaz yöneticisi: {pkg}",
                    ThreatType.Suspicious,
                    ThreatSeverity.High,
                    ThreatCategory.Privilege,
                    "Bilinmeyen cihaz yöneticisi.",
                    packageName: pkg,
                    score: 16,
                    confidence: 65));
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "a11y/admin scan failed");
        }

        return list;
    }

    private async Task<List<ThreatItem>> ScanHostsAsync(CancellationToken ct)
    {
        var list = new List<ThreatItem>();
        try
        {
            var raw = await _root.Shell.RunRootCommandAsync(
                "cat /etc/hosts 2>/dev/null || cat /system/etc/hosts 2>/dev/null", ct)
                .ConfigureAwait(false);

            foreach (var line in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var t = line.Trim();
                if (t.StartsWith('#') || t.Length == 0) continue;
                var parts = t.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                if (parts[0] is "127.0.0.1" or "::1" or "0.0.0.0") continue;

                list.Add(Threat(
                    "hosts manipülasyonu",
                    ThreatType.Malware,
                    ThreatSeverity.High,
                    ThreatCategory.Network,
                    $"Şüpheli satır: {t}",
                    filePath: "/etc/hosts",
                    score: 20,
                    confidence: 75));
                break;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "hosts scan failed");
        }

        return list;
    }

    private async Task<List<ThreatItem>> ScanDataLocalTmpAsync(CancellationToken ct)
    {
        var list = new List<ThreatItem>();
        try
        {
            var raw = await _root.Shell.RunRootCommandAsync(
                "find /data/local/tmp -type f -perm /111 2>/dev/null | head -n 30", ct)
                .ConfigureAwait(false);
            foreach (var path in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var p = path.Trim();
                if (!p.StartsWith('/')) continue;
                list.Add(Threat(
                    $"tmp çalıştırılabilir: {Last(p)}",
                    ThreatType.Suspicious,
                    ThreatSeverity.Medium,
                    ThreatCategory.File,
                    "Dropper olabilir.",
                    filePath: p,
                    score: 12,
                    confidence: 60));
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "tmp scan failed");
        }

        return list;
    }

    private static bool IsTrusted(string packageName) =>
        TrustedPrefixes.Any(p => packageName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static ThreatItem Threat(
        string name,
        ThreatType type,
        ThreatSeverity severity,
        ThreatCategory category,
        string description,
        string packageName = "",
        string filePath = "",
        int score = 10,
        int confidence = 70) => new()
    {
        Name = name,
        Type = type,
        Severity = severity,
        Category = category,
        Description = description,
        PackageName = packageName,
        FilePath = filePath,
        IsSystem = category is ThreatCategory.System or ThreatCategory.Integrity,
        RiskScore = score,
        ConfidenceScore = confidence,
        RemediationAdvice = "Root Modu ile karantinaya alın veya Magisk üzerinden doğrulayın."
    };

    private static string Last(string path)
    {
        var i = path.LastIndexOf('/');
        return i >= 0 ? path[(i + 1)..] : path;
    }

    private static void Report(IProgress<ScanProgress>? progress, string phase, int pct, int threats = 0) =>
        progress?.Report(new ScanProgress
        {
            Phase = phase,
            Percentage = pct,
            ThreatsFound = threats,
            ElapsedTime = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
        });
}
