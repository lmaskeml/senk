using System.IO.Compression;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Security.Root;
using Serilog;

namespace AndroidManager.Security.Services;

/// <summary>
/// YKB / TR banka root gizleme — 3 katman: DenyList+Shamiko, PIF, HMA (paket taramasi).
/// Magisk UI tik'ine guvenilmez; CLI + modul + HMA birlikte gerekir.
/// </summary>
public sealed class BankRootHideService : IBankRootHideService
{
    private const string YkbPackage = "com.ykb.android";
    private const string GmsPackage = "com.google.android.gms";

    private static readonly string[] ShamikoModuleIds = ["zygisk_shamiko", "shamiko"];
    private static readonly string[] PifModuleIds = ["playintegrityfix", "play_integrity_fix", "pif"];
    private static readonly string[] LsposedModuleIds = ["zygisk_lsposed", "lsposed"];
    private const string LsposedManagerPackage = "org.lsposed.manager";

    private static readonly string[] LsposedManagerDevicePaths =
    [
        "/data/adb/lspd/manager.apk",
        "/data/adb/modules/zygisk_lsposed/manager.apk"
    ];

    private static readonly string[] HmaPackages =
    [
        "com.tsng.hidemyapplist",
        "com.tsng.hidemyapplist.pro",
        "icu.nullptr.hidemyapplist"
    ];

    private static readonly string[] PackagesToHideFromYkb =
    [
        "com.topjohnwu.magisk",
        "com.tsng.hidemyapplist",
        "icu.nullptr.hidemyapplist",
        "org.lsposed.manager",
        "com.termux",
        "com.android.vending",
        "com.google.android.gms",
        "bin.mt.plus",
        "com.revanced.manager",
        "com.android.shell"
    ];

    private readonly RootManager _root;
    private readonly IAdbService _adb;
    private readonly IMagiskModuleService _modules;
    private readonly IMagiskDenyListService _denyList;
    private readonly ILogger _logger;

    public BankRootHideService(
        RootManager root,
        IAdbService adb,
        IMagiskModuleService modules,
        IMagiskDenyListService denyList,
        ILogger? logger = null)
    {
        _root = root;
        _adb = adb;
        _modules = modules;
        _denyList = denyList;
        _logger = logger ?? Log.ForContext<BankRootHideService>();
    }

    public async Task<BankRootHideAudit> AuditAsync(CancellationToken cancellationToken = default)
    {
        var checks = new List<BankHideCheckItem>();

        if (!await _root.EnsureActiveAsync(cancellationToken).ConfigureAwait(false))
        {
            checks.Add(Fail("root", "Root (su)", "Magisk/KernelSU izni yok", "Magisk'de Android Manager'a su verin"));
            return BuildAudit(checks);
        }

        checks.Add(Pass("root", "Root (su)", "Aktif", ""));

        // Zygisk
        var zygisk = await QueryMagiskSettingAsync("zygisk", cancellationToken).ConfigureAwait(false);
        checks.Add(zygisk == "1"
            ? Pass("zygisk", "Zygisk", "Acik", "")
            : Fail("zygisk", "Zygisk", "Kapali", "Magisk > Ayarlar > Zygisk ac, reboot"));

        // Enforce DenyList — Shamiko icin KAPALI
        var denySnap = await _denyList.ListAsync(cancellationToken).ConfigureAwait(false);
        checks.Add(!denySnap.EnforceEnabled
            ? Pass("enforce", "Enforce DenyList", "Kapali (Shamiko OK)", "")
            : Fail("enforce", "Enforce DenyList", "ACIK — Shamiko calismaz", "Root Modu > Enforce kapat"));

        // Moduller
        IReadOnlyList<MagiskModuleInfo> mods;
        MagiskEnvironment env;
        try
        {
            var snap = await _modules.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            mods = snap.Modules;
            env = snap.Environment;
        }
        catch (Exception ex)
        {
            checks.Add(Warn("modules", "Magisk modulleri", ex.Message, "Modul listesi okunamadi"));
            mods = [];
            env = new MagiskEnvironment();
        }

        var shamiko = FindModule(mods, ShamikoModuleIds);
        checks.Add(shamiko is not null && shamiko.IsEnabled && !shamiko.RemovePending
            ? Pass("shamiko", "Shamiko", shamiko.DisplayName, "")
            : Fail("shamiko", "Shamiko", shamiko is null ? "Kurulu degil" : "Kapali/kaldirilacak",
                "Shamiko Zygisk modulunu kur (Magisk > Modul zip kur)"));

        var pif = FindModule(mods, PifModuleIds);
        checks.Add(pif is not null && pif.IsEnabled
            ? Pass("pif", "Play Integrity Fix", pif.DisplayName, pif.Description.Length > 0 ? "" : "Action/autopif4 ile fingerprint guncelle")
            : Fail("pif", "Play Integrity Fix", "Kurulu degil",
                "PlayIntegrityFork kur; modul Action ile pif.json/autopif4 calistir"));

        if (pif is not null && pif.IsEnabled)
        {
            var hasPifJson = await FileExistsAsync("/data/adb/modules/playintegrityfix/pif.json", cancellationToken)
                .ConfigureAwait(false)
                || await FileExistsAsync("/data/adb/pif.json", cancellationToken).ConfigureAwait(false);
            checks.Add(hasPifJson
                ? Pass("pif_json", "PIF fingerprint", "pif.json mevcut", "")
                : Warn("pif_json", "PIF fingerprint", "pif.json yok", "PIFork Action > autopif4 calistir"));
        }

        var lsp = FindModule(mods, LsposedModuleIds);
        checks.Add(lsp is not null && lsp.IsEnabled
            ? Pass("lsposed", "Zygisk LSPosed", lsp.DisplayName, "")
            : Fail("lsposed", "LSPosed", "Kurulu degil",
                "Zygisk LSPosed modulunu kur + LSPosed Manager APK"));

        var lspMgr = await PackageInstalledAsync(LsposedManagerPackage, cancellationToken).ConfigureAwait(false);
        checks.Add(lspMgr
            ? Pass("lsposed_mgr", "LSPosed Manager APK", LsposedManagerPackage,
                "Uygulama listesinden ac — bildirime dokunma")
            : Fail("lsposed_mgr", "LSPosed Manager APK", "Kurulu degil / bildirim calismiyor",
                "senk > LSPosed Manager APK kur (parasitik bildirim yerine)"));

        // Magisk APK gizleme
        var magiskHidden = env.ManagerHidden || !await PackageInstalledAsync("com.topjohnwu.magisk", cancellationToken)
            .ConfigureAwait(false);
        checks.Add(magiskHidden
            ? Pass("magisk_hide", "Magisk APK gizli", env.HiddenManagerPackage.Length > 0
                ? $"Paket: {env.HiddenManagerPackage}"
                : "Orijinal paket gorunmuyor", "")
            : Fail("magisk_hide", "Magisk APK", "com.topjohnwu.magisk hala gorunur",
                "Magisk > Ayarlar > Magisk uygulamasini gizle (rastgele isim)"));

        // HMA
        var hmaPkg = await FindInstalledPackageAsync(HmaPackages, cancellationToken).ConfigureAwait(false);
        checks.Add(hmaPkg is not null
            ? Pass("hma", "Hide My Applist", hmaPkg, "LSPosed'da HMA aktif + YKB'ye sablon uygula")
            : Fail("hma", "Hide My Applist", "Kurulu degil",
                "HMA APK kur + LSPosed scope + YKB icin App Manager sablonu (asagida)"));

        // YKB denylist CLI
        var ykbLines = denySnap.Entries.Count(e =>
            e.Package.Equals(YkbPackage, StringComparison.OrdinalIgnoreCase));
        checks.Add(ykbLines >= 1
            ? Pass("ykb_denylist", "YKB DenyList (CLI)", $"{ykbLines} satir — Magisk UI tik ONEMSIZ", "")
            : Warn("ykb_denylist", "YKB DenyList", "CLI'de yok", "YKB kalici pin butonuna bas"));

        // HMA yapilandirma — sadece uyari (otomasyon yok)
        checks.Add(Warn("hma_config", "HMA sablon (manuel)",
            "YKB icin Magisk/LSPosed/Termux vb. gizlenmeli",
            HmaTemplateHint));

        return BuildAudit(checks);
    }

    public async Task<BankRootHideActionResult> ApplyDenyListLayerAsync(CancellationToken cancellationToken = default)
    {
        var pin = await _denyList.PinBankPackagesAsync(cancellationToken).ConfigureAwait(false);
        return new BankRootHideActionResult
        {
            Success = pin.Success,
            Message = pin.Message
        };
    }

    public async Task<BankRootHideActionResult> ClearYkbAppDataAsync(CancellationToken cancellationToken = default)
    {
        if (_adb.SelectedDevice is null)
            return FailMsg("Cihaz bagli degil");

        await _adb.ExecuteShellAsync($"am force-stop {YkbPackage}", cancellationToken).ConfigureAwait(false);
        var clear = await _adb.ExecuteShellAsync($"pm clear {YkbPackage}", cancellationToken).ConfigureAwait(false);
        _logger.Information("[BankHide] pm clear YKB: {Out}", clear.Trim());

        return new BankRootHideActionResult
        {
            Success = !clear.Contains("Error", StringComparison.OrdinalIgnoreCase),
            Message = clear.Trim().Length > 0 ? clear.Trim() : "YKB verileri temizlendi"
        };
    }

    public async Task<BankRootHideActionResult> ClearGmsCacheAsync(CancellationToken cancellationToken = default)
    {
        if (!await _root.EnsureActiveAsync(cancellationToken).ConfigureAwait(false))
            return FailMsg("Root gerekli — GMS cache temizligi icin su izni verin");

        await _root.Shell.RunRootCommandAsync($"am force-stop {GmsPackage}", TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);
        var out1 = await _root.Shell.RunRootCommandAsync(
            $"rm -rf /data/data/{GmsPackage}/cache/* /data/data/{GmsPackage}/code_cache/* 2>/dev/null; echo GMS_CACHE_OK",
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);

        return new BankRootHideActionResult
        {
            Success = out1.Contains("GMS_CACHE_OK", StringComparison.Ordinal),
            Message = "Google Play Hizmetleri onbellegi temizlendi. Reboot onerilir."
        };
    }

    public async Task<BankRootHideActionResult> InstallLsposedManagerApkAsync(
        CancellationToken cancellationToken = default)
    {
        if (_adb.SelectedDevice is null)
            return FailMsg("Cihaz bagli degil");

        var localApk = await ResolveManagerApkPathAsync(cancellationToken).ConfigureAwait(false);
        if (localApk is null)
        {
            return FailMsg(
                "manager.apk bulunamadi.\n\n" +
                "1) scripts\\fetch-bank-stack.ps1 calistir\n" +
                "2) veya LSPosed.zip icinden manager.apk cikar\n" +
                "3) reboot sonrasi tekrar dene (cihazda /data/adb/lspd/manager.apk olusur)");
        }

        _logger.Information("[LSPosed] Installing manager from {Path}", localApk);
        var install = await _adb.InstallLocalPackagesAsync([localApk], cancellationToken).ConfigureAwait(false);
        if (!install.Success)
        {
            return new BankRootHideActionResult
            {
                Success = false,
                Message = "Manager APK kurulamadi: " + install.Message + "\n\n" +
                          "Alternatif: telefonda dosya yoneticisiyle manager.apk'ya dokun.\n" +
                          "Bildirimdeki 'Kabul' parasitik mod — bilinen bug, kullanma."
            };
        }

        // Launcher'dan acmayi dene
        await _adb.ExecuteShellAsync(
            $"monkey -p {LsposedManagerPackage} -c android.intent.category.LAUNCHER 1 2>/dev/null",
            cancellationToken).ConfigureAwait(false);

        return new BankRootHideActionResult
        {
            Success = true,
            Message =
                "LSPosed Manager kuruldu.\n\n" +
                "Uygulama cekmecesinden 'LSPosed' ac.\n" +
                "Bildirime DOKUNMA (parasitik mod hata verir).\n\n" +
                "Acilmazsa: arama/telefon *#*#5776733#*#*\n" +
                "Hâlâ olmazsa reboot → tekrar Manager APK kur."
        };
    }

    private async Task<string?> ResolveManagerApkPathAsync(CancellationToken cancellationToken)
    {
        // PC: fetch script cikarimi
        var candidates = new List<string>
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tools", "bank-stack", "manager.apk")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "tools", "bank-stack", "manager.apk")),
            @"C:\senk\tools\bank-stack\manager.apk"
        };

        var zipPaths = new[]
        {
            @"C:\senk\tools\bank-stack\embedded\LSPosed.zip",
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "tools", "bank-stack", "embedded", "LSPosed.zip"))
        };

        foreach (var c in candidates.Distinct())
        {
            if (File.Exists(c))
                return c;
        }

        foreach (var zip in zipPaths.Distinct())
        {
            if (!File.Exists(zip))
                continue;
            var dest = Path.Combine(Path.GetDirectoryName(zip)!, "..", "manager.apk");
            dest = Path.GetFullPath(dest);
            if (TryExtractManagerFromZip(zip, dest))
                return dest;
        }

        // Cihazdan cek
        if (await _root.EnsureActiveAsync(cancellationToken).ConfigureAwait(false))
        {
            var tmp = Path.Combine(Path.GetTempPath(), "lsposed_manager_" + Guid.NewGuid().ToString("N") + ".apk");
            foreach (var remote in LsposedManagerDevicePaths)
            {
                var exists = await _root.Shell.RunRootCommandAsync(
                    $"test -f {remote} && echo YES",
                    TimeSpan.FromSeconds(8),
                    cancellationToken).ConfigureAwait(false);
                if (!exists.Contains("YES", StringComparison.Ordinal))
                    continue;

                var serial = _adb.SelectedDevice?.Serial;
                var (code, _) = await _adb.RunHostAdbAsync(
                    serial,
                    $"pull \"{remote}\" \"{tmp}\"",
                    TimeSpan.FromMinutes(2),
                    cancellationToken).ConfigureAwait(false);
                if (code == 0 && File.Exists(tmp))
                    return tmp;
            }
        }

        return null;
    }

    internal static bool TryExtractManagerFromZip(string zipPath, string destPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.GetEntry("manager.apk");
            if (entry is null)
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
            return File.Exists(destPath);
        }
        catch
        {
            return false;
        }
    }

    internal const string HmaTemplateHint =
        "HMA > Templates > + > App Manager\n" +
        "Gizlenecekler: Magisk (gizli ad), LSPosed Manager, Termux, MT Manager, Magisk modulleri\n" +
        "Hedef: Yapı Kredi > bu sablonu uygula\n" +
        "LSPosed > HMA > YKB + GMS scope acik olmali";

    internal static readonly string PackagesToHideHint =
        string.Join(", ", PackagesToHideFromYkb.Take(8)) + ", …";

    private async Task<string?> QueryMagiskSettingAsync(string key, CancellationToken ct)
    {
        var raw = await _root.Shell.RunRootCommandAsync(
            $"magisk --sqlite \"SELECT value FROM settings WHERE key='{key}' LIMIT 1;\" 2>/dev/null",
            TimeSpan.FromSeconds(10),
            ct).ConfigureAwait(false);
        var line = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    private async Task<bool> PackageInstalledAsync(string package, CancellationToken ct)
    {
        var o = await _adb.ExecuteShellAsync($"pm path {package} 2>/dev/null", ct).ConfigureAwait(false);
        return o.Contains("package:", StringComparison.Ordinal);
    }

    private async Task<string?> FindInstalledPackageAsync(IEnumerable<string> packages, CancellationToken ct)
    {
        foreach (var p in packages)
        {
            if (await PackageInstalledAsync(p, ct).ConfigureAwait(false))
                return p;
        }

        return null;
    }

    private async Task<bool> FileExistsAsync(string path, CancellationToken ct)
    {
        var o = await _root.Shell.RunRootCommandAsync(
            $"test -f {path} && echo YES",
            TimeSpan.FromSeconds(8),
            ct).ConfigureAwait(false);
        return o.Contains("YES", StringComparison.Ordinal);
    }

    private static MagiskModuleInfo? FindModule(IReadOnlyList<MagiskModuleInfo> mods, string[] ids)
    {
        foreach (var id in ids)
        {
            var m = mods.FirstOrDefault(x =>
                x.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ||
                x.Id.Contains(id, StringComparison.OrdinalIgnoreCase));
            if (m is not null)
                return m;
        }

        return mods.FirstOrDefault(m =>
            ids.Any(id => m.Id.Contains(id, StringComparison.OrdinalIgnoreCase) ||
                          m.Name.Contains(id, StringComparison.OrdinalIgnoreCase)));
    }

    private static BankRootHideAudit BuildAudit(List<BankHideCheckItem> checks)
    {
        return new BankRootHideAudit
        {
            Checks = checks,
            PassCount = checks.Count(c => c.Status == BankHideCheckStatus.Pass),
            FailCount = checks.Count(c => c.Status == BankHideCheckStatus.Fail),
            WarnCount = checks.Count(c => c.Status == BankHideCheckStatus.Warn)
        };
    }

    private static BankHideCheckItem Pass(string id, string title, string detail, string fix) =>
        new() { Id = id, Title = title, Detail = detail, Status = BankHideCheckStatus.Pass, FixHint = fix };

    private static BankHideCheckItem Fail(string id, string title, string detail, string fix) =>
        new() { Id = id, Title = title, Detail = detail, Status = BankHideCheckStatus.Fail, FixHint = fix };

    private static BankHideCheckItem Warn(string id, string title, string detail, string fix) =>
        new() { Id = id, Title = title, Detail = detail, Status = BankHideCheckStatus.Warn, FixHint = fix };

    private static BankRootHideActionResult FailMsg(string msg) =>
        new() { Success = false, Message = msg };
}
