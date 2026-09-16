using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Security.Root;
using Serilog;

namespace AndroidManager.Security.Services;

public sealed class MagiskDenyListService : IMagiskDenyListService
{
    private const string PinModuleId = "am_denylist_pin";

    private static readonly string[] BankPackages =
    [
        "com.ykb.android",
        "com.akbank.android.apps.akbank_direkt",
        "com.pozitron.iscep",
        "com.isbank.iscep",
        "com.garanti.cepsubesi",
        "com.ziraat.ziraatmobil",
        "com.tmobtech.halkbank",
        "com.denizbank.mobildeniz",
        "com.vakifbank.mobile",
        "com.teb",
        "com.ingbanktr.ingmobil",
        "com.google.android.gms",
        "com.android.vending"
    ];

    private static readonly (string Pkg, string Proc)[] ExtraProcesses =
    [
        ("com.ykb.android", "com.ykb.android"),
        ("com.ykb.android", "com.ykb.android:MAIN_PROCESS"),
        ("com.ykb.android", "com.ykb.android:hce"),
        ("com.ykb.android", "com.ykb.android:remote"),
        ("com.google.android.gms", "com.google.android.gms"),
        ("com.google.android.gms", "com.google.android.gms.unstable")
    ];

    private readonly RootManager _root;
    private readonly IAdbService _adb;
    private readonly ILogger _logger;

    public MagiskDenyListService(RootManager root, IAdbService adb, ILogger? logger = null)
    {
        _root = root;
        _adb = adb;
        _logger = logger ?? Log.ForContext<MagiskDenyListService>();
    }

    public async Task<MagiskDenyListSnapshot> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!await _root.EnsureActiveAsync(cancellationToken).ConfigureAwait(false))
            return new MagiskDenyListSnapshot { Raw = "Root yok" };

        var magisk = await ResolveMagiskAsync(cancellationToken).ConfigureAwait(false);
        if (magisk is null)
            return new MagiskDenyListSnapshot { Raw = "magisk binary bulunamadı" };

        var status = await _root.Shell.RunRootCommandAsync(
            $"{magisk} --denylist status 2>&1; echo __SEP__; {magisk} --denylist ls 2>&1",
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);

        var parts = status.Split("__SEP__", 2, StringSplitOptions.None);
        var statusPart = parts.Length > 0 ? parts[0] : "";
        var listPart = parts.Length > 1 ? parts[1] : status;

        var enforce = false;
        if (statusPart.Contains("not enforced", StringComparison.OrdinalIgnoreCase))
            enforce = false;
        else if (statusPart.Contains("is enforced", StringComparison.OrdinalIgnoreCase))
            enforce = true;

        return new MagiskDenyListSnapshot
        {
            EnforceEnabled = enforce,
            Entries = ParseLs(listPart),
            Raw = listPart.Trim()
        };
    }

    public async Task<bool> AddAsync(string package, string? process = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(package))
            return false;
        if (!await _root.EnsureActiveAsync(cancellationToken).ConfigureAwait(false))
            return false;

        var magisk = await ResolveMagiskAsync(cancellationToken).ConfigureAwait(false);
        if (magisk is null)
            return false;

        var proc = string.IsNullOrWhiteSpace(process) ? package : process;
        var cli = await _root.Shell.RunRootCommandAsync(
            $"{magisk} --denylist add {package} {proc} 2>&1",
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);

        // UI wipe'a karşı sqlite çift yazım
        var pkgEsc = EscapeSql(package);
        var procEsc = EscapeSql(proc);
        await _root.Shell.RunRootCommandAsync(
            $"{magisk} --sqlite \"INSERT OR IGNORE INTO denylist (package_name,process) VALUES('{pkgEsc}','{procEsc}');\" 2>&1",
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(cli) ||
               cli.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
               !cli.Contains("error", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> SetEnforceAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (!await _root.EnsureActiveAsync(cancellationToken).ConfigureAwait(false))
            return false;

        var magisk = await ResolveMagiskAsync(cancellationToken).ConfigureAwait(false);
        if (magisk is null)
            return false;

        var action = enabled ? "enable" : "disable";
        await _root.Shell.RunRootCommandAsync(
            $"{magisk} --denylist {action} 2>&1",
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);

        var val = enabled ? "1" : "0";
        await _root.Shell.RunRootCommandAsync(
            $"{magisk} --sqlite \"REPLACE INTO settings (key,value) VALUES('denylist',{val});\" 2>&1",
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);

        var snap = await ListAsync(cancellationToken).ConfigureAwait(false);
        return snap.EnforceEnabled == enabled;
    }

    public async Task<MagiskDenyListPinResult> PinBankPackagesAsync(CancellationToken cancellationToken = default)
    {
        if (!await _root.EnsureActiveAsync(cancellationToken).ConfigureAwait(false))
        {
            return new MagiskDenyListPinResult
            {
                Success = false,
                Message = "Root erişimi yok — Magisk'de bu uygulamaya su izni verin."
            };
        }

        // Magisk Manager Red Listesi acikken kayit silebiliyor — once durdur
        await ForceStopMagiskManagerAsync(cancellationToken).ConfigureAwait(false);
        await SetEnforceAsync(false, cancellationToken).ConfigureAwait(false);

        var targets = new HashSet<(string Pkg, string Proc)>();
        foreach (var pkg in BankPackages)
            targets.Add((pkg, pkg));

        foreach (var e in ExtraProcesses)
            targets.Add(e);

        foreach (var proc in await DiscoverProcessesAsync("com.ykb.android", cancellationToken).ConfigureAwait(false))
            targets.Add(("com.ykb.android", proc));

        var ok = 0;
        foreach (var (pkg, proc) in targets)
        {
            if (await AddAsync(pkg, proc, cancellationToken).ConfigureAwait(false))
                ok++;
        }

        var moduleOk = await InstallBootPinModuleAsync(targets, cancellationToken).ConfigureAwait(false);

        // Watchdog'u hemen baslat (reboot beklemeden)
        await _root.Shell.RunRootCommandAsync(
            "pkill -f am_denylist_watchdog 2>/dev/null; " +
            "nohup sh /data/adb/modules/am_denylist_pin/watchdog.sh >/dev/null 2>&1 & " +
            "nohup sh /data/adb/modules_update/am_denylist_pin/watchdog.sh >/dev/null 2>&1 & " +
            "true",
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);

        var snap = await ListAsync(cancellationToken).ConfigureAwait(false);
        var ykb = snap.Entries.Count(e =>
            e.Package.Equals("com.ykb.android", StringComparison.OrdinalIgnoreCase));
        var ykbLines = string.Join("\n", snap.Entries
            .Where(e => e.Package.Equals("com.ykb.android", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Display)
            .Take(12));

        _logger.Information(
            "[DenyList] Pin: {Ok}/{Total} YKB={Ykb} module={Mod} Enforce={Enf}",
            ok, targets.Count, ykb, moduleOk, snap.EnforceEnabled);

        var msg = ykb > 0
            ? $"CLI gercek durum: YKB={ykb} satir (Magisk UI tik'i ONEMSIZ).\n" +
              $"{ykbLines}\n\n" +
              $"Boot pin={(moduleOk ? "OK" : "fail")} | Enforce={(snap.EnforceEnabled ? "ACIK-KAPAT" : "kapali")} | Watchdog calisiyor.\n\n" +
              "ARTIK Magisk > Red Listesi'ne YKB icin BAKMA. Tik kaldirilsa bile CLI doluysa Shamiko gizler.\n" +
              "Test: YKB veri temizle → ac. Root uyarisi yoksa basarili.\n" +
              "Dogrulama: senk DenyList yenile (Magisk UI degil)."
            : "YKB hala CLI denylist'te yok. su / magisk kontrol et.";

        return new MagiskDenyListPinResult
        {
            Success = ykb > 0,
            AddedOrPresent = ok,
            EnforceDisabled = !snap.EnforceEnabled,
            Message = msg
        };
    }

    private async Task ForceStopMagiskManagerAsync(CancellationToken cancellationToken)
    {
        // Gizli Magisk paket adi dahil
        await _root.Shell.RunRootCommandAsync(
            "for p in com.topjohnwu.magisk io.github.vvb2060.magisk com.kontrayd.mymagisk; do " +
            "am force-stop \"$p\" 2>/dev/null; done; " +
            "pm list packages 2>/dev/null | grep -i magisk | sed 's/package://' | while read p; do " +
            "am force-stop \"$p\" 2>/dev/null; done; true",
            TimeSpan.FromSeconds(25),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> DiscoverProcessesAsync(string package, CancellationToken cancellationToken)
    {
        var dump = await _root.Shell.RunRootCommandAsync(
            $"dumpsys package {package} 2>/dev/null",
            TimeSpan.FromSeconds(40),
            cancellationToken).ConfigureAwait(false);

        var set = new HashSet<string>(StringComparer.Ordinal)
        {
            package
        };

        foreach (Match m in Regex.Matches(dump, @"processName=([^\s,}]+)"))
        {
            var p = m.Groups[1].Value.Trim();
            if (p.Length > 0 && (p == package || p.StartsWith(package + ":", StringComparison.Ordinal)))
                set.Add(p);
        }

        foreach (Match m in Regex.Matches(dump, "android:process=\"?([^\"\\s}]+)\"?"))
        {
            var p = m.Groups[1].Value.Trim();
            if (p.StartsWith(':'))
                p = package + p;
            if (p.Length > 0 && (p == package || p.StartsWith(package, StringComparison.Ordinal)))
                set.Add(p);
        }

        // Çalışan + zygote child isimleri (dinamik kanal)
        var ps = await _root.Shell.RunRootCommandAsync(
            $"ps -A -o NAME 2>/dev/null | grep '{package}' | head -n 60",
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        foreach (var line in ps.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var name = line.Trim();
            if (name.StartsWith(package, StringComparison.Ordinal))
                set.Add(name);
        }

        return set.ToList();
    }

    private async Task<bool> InstallBootPinModuleAsync(
        IReadOnlyCollection<(string Pkg, string Proc)> targets,
        CancellationToken cancellationToken)
    {
        try
        {
            var tmp = Path.Combine(Path.GetTempPath(), "am_denylist_pin_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                await File.WriteAllTextAsync(
                    Path.Combine(tmp, "module.prop"),
                    """
                    id=am_denylist_pin
                    name=AM DenyList Pin (YKB+)
                    version=v1.2.0
                    versionCode=120
                    author=AndroidManager
                    description=YKB dinamik surec watchdog + boot pin. Magisk UI tik'ine guvenme.
                    """,
                    cancellationToken).ConfigureAwait(false);

                var pinLines = new StringBuilder();
                foreach (var (pkg, proc) in targets.OrderBy(t => t.Pkg).ThenBy(t => t.Proc))
                {
                    pinLines.AppendLine($"magisk --denylist add {pkg} {proc} >/dev/null 2>&1");
                    pinLines.AppendLine(
                        $"magisk --sqlite \"INSERT OR IGNORE INTO denylist (package_name,process) VALUES('{EscapeSql(pkg)}','{EscapeSql(proc)}');\" >/dev/null 2>&1");
                }

                var watchdog = """
                    #!/system/bin/sh
                    # am_denylist_watchdog — YKB yeni surec acinca denylist'e ekle
                    export PATH=/system/bin:/system/xbin:$PATH
                    while true; do
                      magisk --denylist disable >/dev/null 2>&1
                      for p in $(ps -A -o NAME 2>/dev/null | grep 'com.ykb.android' | sort -u); do
                        magisk --denylist add com.ykb.android "$p" >/dev/null 2>&1
                        magisk --sqlite "INSERT OR IGNORE INTO denylist (package_name,process) VALUES('com.ykb.android','$p');" >/dev/null 2>&1
                      done
                      magisk --denylist add com.ykb.android com.ykb.android >/dev/null 2>&1
                      magisk --denylist add com.ykb.android com.ykb.android:MAIN_PROCESS >/dev/null 2>&1
                      magisk --denylist add com.ykb.android com.ykb.android:hce >/dev/null 2>&1
                      magisk --denylist add com.ykb.android com.ykb.android:remote >/dev/null 2>&1
                      sleep 20
                    done
                    """;

                await File.WriteAllTextAsync(
                    Path.Combine(tmp, "watchdog.sh"),
                    watchdog.Replace("\r\n", "\n"),
                    new UTF8Encoding(false),
                    cancellationToken).ConfigureAwait(false);

                var service = new StringBuilder();
                service.AppendLine("#!/system/bin/sh");
                service.AppendLine("MODDIR=${0%/*}");
                service.AppendLine("i=0");
                service.AppendLine("while [ $i -lt 45 ]; do");
                service.AppendLine("  magisk -v >/dev/null 2>&1 && break");
                service.AppendLine("  sleep 1");
                service.AppendLine("  i=$((i+1))");
                service.AppendLine("done");
                service.AppendLine("magisk --denylist disable >/dev/null 2>&1");
                service.AppendLine("magisk --sqlite \"REPLACE INTO settings (key,value) VALUES('denylist',0);\" >/dev/null 2>&1");
                service.Append(pinLines);
                service.AppendLine("pkill -f am_denylist_watchdog 2>/dev/null");
                service.AppendLine("# watchdog kimligi icin argv0");
                service.AppendLine("sh -c 'exec -a am_denylist_watchdog sh \"$0\"' \"$MODDIR/watchdog.sh\" >/dev/null 2>&1 &");
                service.AppendLine("exit 0");

                await File.WriteAllTextAsync(
                    Path.Combine(tmp, "service.sh"),
                    service.ToString().Replace("\r\n", "\n"),
                    new UTF8Encoding(false),
                    cancellationToken).ConfigureAwait(false);

                // post-fs-data: daha erken pin (UI wipe oncesi)
                var early = new StringBuilder();
                early.AppendLine("#!/system/bin/sh");
                early.AppendLine("magisk --denylist disable >/dev/null 2>&1");
                early.Append(pinLines);
                early.AppendLine("exit 0");
                await File.WriteAllTextAsync(
                    Path.Combine(tmp, "post-fs-data.sh"),
                    early.ToString().Replace("\r\n", "\n"),
                    new UTF8Encoding(false),
                    cancellationToken).ConfigureAwait(false);

                var zipPath = Path.Combine(Path.GetTempPath(), PinModuleId + ".zip");
                if (File.Exists(zipPath))
                    File.Delete(zipPath);
                ZipFile.CreateFromDirectory(tmp, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);

                return await DeployModuleZipAsync(zipPath, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "DenyList pin modulu kurulamadi");
            return false;
        }
    }

    private async Task<bool> DeployModuleZipAsync(string localZip, CancellationToken cancellationToken)
    {
        var remote = $"/data/local/tmp/{PinModuleId}_{DateTime.UtcNow.Ticks}.zip";
        var serial = _adb.SelectedDevice?.Serial;
        var (exit, pushOut) = await _adb.RunHostAdbAsync(
            serial,
            $"push \"{localZip}\" \"{remote}\"",
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);

        if (exit != 0)
        {
            _logger.Warning("Pin module push failed: {Out}", pushOut);
            return false;
        }

        var magisk = await ResolveMagiskAsync(cancellationToken).ConfigureAwait(false);
        if (magisk is not null)
        {
            var outp = await _root.Shell.RunRootCommandAsync(
                $"{magisk} --install-module {remote} 2>&1; echo EXIT:$?",
                TimeSpan.FromMinutes(2),
                cancellationToken).ConfigureAwait(false);
            await _root.Shell.RunRootCommandAsync($"rm -f {remote}", TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
            if (outp.Contains("EXIT:0", StringComparison.Ordinal) ||
                outp.Contains("- Done", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var deploy = await _root.Shell.RunRootCommandAsync(
            $"MOD=/data/adb/modules_update/{PinModuleId}; " +
            "rm -rf \"$MOD\"; mkdir -p \"$MOD\"; " +
            $"unzip -o {remote} -d \"$MOD\" >/dev/null 2>&1; " +
            "rm -rf \"$MOD/META-INF\"; " +
            "chmod 755 \"$MOD/service.sh\" \"$MOD/watchdog.sh\" \"$MOD/post-fs-data.sh\" 2>/dev/null; " +
            $"mkdir -p /data/adb/modules/{PinModuleId}; " +
            $"touch /data/adb/modules/{PinModuleId}/update; " +
            $"rm -f {remote}; " +
            "test -f \"$MOD/module.prop\" && echo DEPLOY_OK || echo DEPLOY_FAIL",
            TimeSpan.FromMinutes(1),
            cancellationToken).ConfigureAwait(false);

        return deploy.Contains("DEPLOY_OK", StringComparison.Ordinal);
    }

    private async Task<string?> ResolveMagiskAsync(CancellationToken cancellationToken)
    {
        var candidates = new[]
        {
            "magisk",
            "/debug_ramdisk/magisk",
            "/debug_ramdisk/bin/magisk",
            "/system/bin/magisk",
            "/sbin/magisk",
            "/data/adb/magisk/magisk64",
            "/data/adb/magisk/magisk32",
            "/data/adb/magisk/magisk"
        };

        foreach (var c in candidates)
        {
            var check = await _root.Shell.RunRootCommandAsync(
                $"(command -v {c} >/dev/null 2>&1 || test -x {c}) && echo OK",
                TimeSpan.FromSeconds(8),
                cancellationToken).ConfigureAwait(false);
            if (check.Contains("OK", StringComparison.Ordinal))
                return c;
        }

        var path = (await _root.Shell.RunRootCommandAsync(
            "magisk --path 2>/dev/null",
            TimeSpan.FromSeconds(8),
            cancellationToken).ConfigureAwait(false)).Trim();
        if (!string.IsNullOrWhiteSpace(path) && path.StartsWith('/'))
        {
            foreach (var name in new[] { "/magisk", "/magisk64" })
            {
                var full = path + name;
                var check = await _root.Shell.RunRootCommandAsync(
                    $"test -x {full} && echo OK",
                    TimeSpan.FromSeconds(5),
                    cancellationToken).ConfigureAwait(false);
                if (check.Contains("OK", StringComparison.Ordinal))
                    return full;
            }
        }

        return null;
    }

    private static string EscapeSql(string s) => s.Replace("'", "''", StringComparison.Ordinal);

    internal static List<MagiskDenyListEntry> ParseLs(string raw)
    {
        var list = new List<MagiskDenyListEntry>();
        if (string.IsNullOrWhiteSpace(raw))
            return list;

        foreach (var line in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith("Denied", StringComparison.OrdinalIgnoreCase))
                continue;

            var pipe = t.IndexOf('|');
            if (pipe < 0)
            {
                list.Add(new MagiskDenyListEntry { Package = t, Process = t });
                continue;
            }

            list.Add(new MagiskDenyListEntry
            {
                Package = t[..pipe].Trim(),
                Process = t[(pipe + 1)..].Trim()
            });
        }

        return list;
    }
}
