using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Security.Root;
using Serilog;

namespace AndroidManager.Security.Services;

public sealed class MagiskModuleService : IMagiskModuleService
{
    /// <summary>
    /// Magisk uygulaması gizlenince paket adı rastgele olur; modüller yine /data/adb/modules altındadır.
    /// find + while, glob/su tırnak sorunlarını ve gizli yönetici UI'sini bypass eder.
    /// </summary>
    private const string SnapshotScript =
        "echo AM_ENV_BEGIN; " +
        "echo AM_VER=$(magisk -v 2>/dev/null | head -1); " +
        "echo AM_BIN=$(command -v magisk 2>/dev/null); " +
        "echo AM_MPATH=$(magisk --path 2>/dev/null); " +
        "test -x /debug_ramdisk/magisk && echo AM_BIN2=/debug_ramdisk/magisk; " +
        "test -x /data/adb/magisk/magisk64 && echo AM_BIN2=/data/adb/magisk/magisk64; " +
        "test -f /data/adb/magisk.db && echo AM_DB=1 || echo AM_DB=0; " +
        "test -d /data/adb/modules && echo AM_DIR=1 || echo AM_DIR=0; " +
        "echo AM_REQ=$(magisk --sqlite \"SELECT value FROM strings WHERE key='requester';\" 2>/dev/null | tail -1); " +
        "echo AM_ENV_END; " +
        "find /data/adb/modules /data/adb/modules_update -mindepth 2 -maxdepth 2 -name module.prop 2>/dev/null | " +
        "while IFS= read -r f; do " +
        "d=${f%/module.prop}; " +
        "echo AM_BEGIN; " +
        "echo AM_PATH=$d; " +
        "test -f \"$d/disable\" && echo AM_DISABLED=1 || echo AM_DISABLED=0; " +
        "test -f \"$d/remove\" && echo AM_REMOVE=1 || echo AM_REMOVE=0; " +
        "test -f \"$d/update\" && echo AM_UPDATE=1 || echo AM_UPDATE=0; " +
        "case $d in */modules_update/*) echo AM_PENDING=1;; *) echo AM_PENDING=0;; esac; " +
        "cat \"$f\"; " +
        "echo AM_END; " +
        "done";

    private static readonly string[] KnownManagerPackages =
    [
        "com.topjohnwu.magisk",
        "io.github.huskydg.magisk",
        "io.github.vvb2060.magisk"
    ];

    private readonly RootManager _root;
    private readonly IAdbService _adb;
    private readonly ILogger _logger;

    public MagiskModuleService(RootManager root, IAdbService adb, ILogger? logger = null)
    {
        _root = root;
        _adb = adb;
        _logger = logger ?? Log.ForContext<MagiskModuleService>();
    }

    public async Task<MagiskModuleSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (!await _root.EnsureActiveAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Root erişimi yok — Magisk/KernelSU izni verin.");

        var dump = await _root.Shell.RunRootCommandAsync(
            SnapshotScript,
            TimeSpan.FromSeconds(60),
            cancellationToken).ConfigureAwait(false);

        var stockPresent = await StockManagerInstalledAsync(cancellationToken).ConfigureAwait(false);
        return new MagiskModuleSnapshot
        {
            Environment = ParseEnvironment(dump, stockPresent),
            Modules = MagiskModulePropParser.ParseDeviceDump(dump)
        };
    }

    public async Task<IReadOnlyList<MagiskModuleInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var snap = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return snap.Modules;
    }

    public async Task<MagiskModuleInstallResult> InstallAsync(
        string zipPath,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        var zipInfo = MagiskModulePropParser.TryReadZip(zipPath);
        if (zipInfo is null)
        {
            return Fail(zipPath, "", Path.GetFileName(zipPath),
                "Geçerli Magisk modülü değil (zip kökünde module.prop yok veya id hatalı).");
        }

        if (!File.Exists(zipPath))
            return Fail(zipPath, zipInfo.Id, zipInfo.Name, "Zip dosyası bulunamadı.");

        if (!await _root.EnsureActiveAsync(cancellationToken).ConfigureAwait(false))
            return Fail(zipPath, zipInfo.Id, zipInfo.Name, "Root erişimi yok — Magisk'de bu uygulamaya su izni verin.");

        var remote = $"/data/local/tmp/am_mod_{zipInfo.Id}_{DateTime.UtcNow.Ticks}.zip";
        status?.Report($"{zipInfo.Name} cihaza kopyalanıyor…");

        try
        {
            if (!await PushZipAsync(zipPath, remote, cancellationToken).ConfigureAwait(false))
                return Fail(zipPath, zipInfo.Id, zipInfo.Name, "Zip cihaza kopyalanamadı (ADB push).");

            await _root.Shell.RunRootCommandAsync(
                $"chmod 644 {remote}",
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);

            status?.Report($"{zipInfo.Name} kuruluyor…");
            var (engine, output) = await RunInstallerAsync(remote, zipInfo.Id, cancellationToken)
                .ConfigureAwait(false);

            _logger.Information("[Magisk] {Engine} install {Id}: {Out}", engine, zipInfo.Id, Truncate(output));

            var success = MagiskModulePropParser.IsInstallSuccess(output) ||
                          await ModuleExistsAsync(zipInfo.Id, cancellationToken).ConfigureAwait(false);

            if (!success)
            {
                return Fail(
                    zipPath,
                    zipInfo.Id,
                    zipInfo.Name,
                    string.IsNullOrWhiteSpace(output)
                        ? "Kurulum çıktı vermedi. Magisk/KernelSU yüklü ve su izni verilmiş olmalı."
                        : Truncate(output));
            }

            return new MagiskModuleInstallResult
            {
                Success = true,
                FilePath = zipPath,
                ModuleId = zipInfo.Id,
                ModuleName = zipInfo.Name,
                Engine = engine,
                RebootRequired = true,
                Message = $"{zipInfo.Name} kuruldu ({engine}). Etkinleşmesi için cihazı yeniden başlatın."
            };
        }
        finally
        {
            try
            {
                await _root.Shell.RunRootCommandAsync(
                    $"rm -f {remote}",
                    TimeSpan.FromSeconds(15),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Geçici Magisk zip silinemedi");
            }
        }
    }

    public async Task<bool> SetEnabledAsync(string moduleId, bool enabled, CancellationToken cancellationToken = default)
    {
        if (!MagiskModulePropParser.IsValidId(moduleId))
            return false;
        if (!await _root.EnsureActiveAsync(cancellationToken).ConfigureAwait(false))
            return false;

        var path = $"/data/adb/modules/{moduleId}/disable";
        var cmd = enabled ? $"rm -f {path}" : $"touch {path}";
        await _root.Shell.RunRootCommandAsync(cmd, TimeSpan.FromSeconds(20), cancellationToken)
            .ConfigureAwait(false);

        var check = await _root.Shell.RunRootCommandAsync(
            $"test -f {path} && echo DISABLED || echo ENABLED",
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);

        var nowDisabled = check.Contains("DISABLED", StringComparison.Ordinal);
        return enabled ? !nowDisabled : nowDisabled;
    }

    public async Task<bool> RequestRemoveAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        if (!MagiskModulePropParser.IsValidId(moduleId))
            return false;
        if (!await _root.EnsureActiveAsync(cancellationToken).ConfigureAwait(false))
            return false;

        await _root.Shell.RunRootCommandAsync(
            $"touch /data/adb/modules/{moduleId}/remove; rm -rf /data/adb/modules_update/{moduleId}",
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);

        var check = await _root.Shell.RunRootCommandAsync(
            $"test -f /data/adb/modules/{moduleId}/remove && echo OK",
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);

        return check.Contains("OK", StringComparison.Ordinal);
    }

    private async Task<bool> PushZipAsync(string localPath, string remotePath, CancellationToken cancellationToken)
    {
        var serial = _adb.SelectedDevice?.Serial;
        var (exitCode, output) = await _adb.RunHostAdbAsync(
            serial,
            $"push \"{localPath}\" \"{remotePath}\"",
            TimeSpan.FromMinutes(5),
            cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            _logger.Warning("Magisk zip push failed ({Code}): {Out}", exitCode, Truncate(output));
            return false;
        }

        var ls = await _adb.ExecuteShellAsync($"ls \"{remotePath}\" 2>/dev/null", cancellationToken)
            .ConfigureAwait(false);
        return ls.Contains(Path.GetFileName(remotePath), StringComparison.Ordinal);
    }

    private async Task<(string Engine, string Output)> RunInstallerAsync(
        string remoteZip,
        string moduleId,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMinutes(8);

        var magisk = await FirstExistingAsync(
            [
                "magisk",
                "/debug_ramdisk/magisk",
                "/debug_ramdisk/bin/magisk",
                "/system/bin/magisk",
                "/sbin/magisk",
                "/data/adb/magisk/magisk64",
                "/data/adb/magisk/magisk32",
                "/data/adb/magisk/magisk"
            ],
            cancellationToken).ConfigureAwait(false);

        if (magisk is null)
        {
            var magiskPath = (await _root.Shell.RunRootCommandAsync(
                "magisk --path 2>/dev/null",
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false)).Trim();
            if (!string.IsNullOrWhiteSpace(magiskPath) && magiskPath.StartsWith('/'))
                magisk = await FirstExistingAsync(
                    [magiskPath + "/magisk", magiskPath + "/magisk64"],
                    cancellationToken).ConfigureAwait(false);
        }

        if (magisk is not null)
        {
            var output = await _root.Shell.RunRootCommandAsync(
                $"{magisk} --install-module {remoteZip}",
                timeout,
                cancellationToken).ConfigureAwait(false);
            if (!LooksMissingBinary(output))
                return ("Magisk", output);
        }

        var ksud = await FirstExistingAsync(
            [
                "ksud",
                "/data/adb/ksud",
                "/data/adb/ksu/bin/ksud"
            ],
            cancellationToken).ConfigureAwait(false);

        if (ksud is not null)
        {
            var output = await _root.Shell.RunRootCommandAsync(
                $"{ksud} module install {remoteZip}",
                timeout,
                cancellationToken).ConfigureAwait(false);
            if (!LooksMissingBinary(output))
                return ("KernelSU", output);
        }

        var apd = await FirstExistingAsync(
            [
                "apd",
                "/data/adb/apd",
                "/data/adb/ap/bin/apd"
            ],
            cancellationToken).ConfigureAwait(false);

        if (apd is not null)
        {
            var output = await _root.Shell.RunRootCommandAsync(
                $"{apd} module install {remoteZip}",
                timeout,
                cancellationToken).ConfigureAwait(false);
            if (!LooksMissingBinary(output))
                return ("APatch", output);
        }

        var extract = await _root.Shell.RunRootCommandAsync(
            "MOD=/data/adb/modules_update/" + moduleId + "; " +
            "mkdir -p \"$MOD\"; " +
            "unzip -o " + remoteZip + " -d \"$MOD\" >/dev/null 2>&1; " +
            "rm -rf \"$MOD/META-INF\"; " +
            "mkdir -p /data/adb/modules/" + moduleId + "; " +
            "touch /data/adb/modules/" + moduleId + "/update; " +
            "test -f \"$MOD/module.prop\" && echo EXTRACT_OK || echo EXTRACT_FAIL",
            timeout,
            cancellationToken).ConfigureAwait(false);

        return ("unzip", extract.Contains("EXTRACT_OK", StringComparison.Ordinal)
            ? "- Done\nEXTRACT_OK"
            : extract);
    }

    private async Task<bool> StockManagerInstalledAsync(CancellationToken cancellationToken)
    {
        foreach (var pkg in KnownManagerPackages)
        {
            try
            {
                var path = await _adb.ExecuteShellAsync($"pm path {pkg} 2>/dev/null", cancellationToken)
                    .ConfigureAwait(false);
                if (path.Contains("package:", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                // paket yok
            }
        }

        return false;
    }

    internal static MagiskEnvironment ParseEnvironment(string dump, bool stockManagerInstalled)
    {
        var ver = ReadEnv(dump, "AM_VER=");
        var bin = ReadEnv(dump, "AM_BIN=");
        var bin2 = ReadEnv(dump, "AM_BIN2=");
        var req = NormalizeRequester(ReadEnv(dump, "AM_REQ="));
        var hasDb = dump.Contains("AM_DB=1", StringComparison.Ordinal);
        var hasDir = dump.Contains("AM_DIR=1", StringComparison.Ordinal);
        var binary = !string.IsNullOrWhiteSpace(bin) ? bin : bin2;
        var core = hasDb || hasDir || !string.IsNullOrWhiteSpace(ver) || !string.IsNullOrWhiteSpace(binary);
        var hidden = core && !stockManagerInstalled;

        var summary = !core
            ? "Magisk çekirdeği görülmedi"
            : hidden
                ? string.IsNullOrWhiteSpace(req)
                    ? "Magisk uygulaması gizlenmiş (yeniden adlandırılmış). Modüller /data/adb/modules üzerinden okunuyor."
                    : $"Magisk uygulaması gizlenmiş ({req}). Modüller /data/adb/modules üzerinden okunuyor."
                : string.IsNullOrWhiteSpace(ver)
                    ? "Magisk çekirdeği mevcut"
                    : "Magisk " + ver;

        return new MagiskEnvironment
        {
            CorePresent = core,
            ManagerHidden = hidden,
            Version = ver,
            BinaryPath = binary,
            HiddenManagerPackage = hidden ? req : "",
            Summary = summary
        };
    }

    private static string ReadEnv(string dump, string key)
    {
        foreach (var raw in dump.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith(key, StringComparison.Ordinal))
                return line[key.Length..].Trim();
        }

        return "";
    }

    private static string NormalizeRequester(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var value = raw.Trim();
        var eq = value.LastIndexOf('=');
        if (eq >= 0)
            value = value[(eq + 1)..].Trim();
        if (value.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("usage", StringComparison.OrdinalIgnoreCase))
            return "";
        return value.Length > 3 && value.Contains('.') ? value : "";
    }

    private async Task<string?> FirstExistingAsync(IReadOnlyList<string> candidates, CancellationToken cancellationToken)
    {
        foreach (var path in candidates)
        {
            var check = path.Contains('/')
                ? $"test -x {path} && echo OK"
                : $"command -v {path} >/dev/null 2>&1 && echo OK";

            var output = await _root.Shell.RunRootCommandAsync(
                check,
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);

            if (output.Contains("OK", StringComparison.Ordinal))
                return path;
        }

        return null;
    }

    private async Task<bool> ModuleExistsAsync(string moduleId, CancellationToken cancellationToken)
    {
        var output = await _root.Shell.RunRootCommandAsync(
            $"test -f /data/adb/modules_update/{moduleId}/module.prop || test -f /data/adb/modules/{moduleId}/module.prop && echo OK",
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        return output.Contains("OK", StringComparison.Ordinal);
    }

    private static bool LooksMissingBinary(string output) =>
        output.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("No such file", StringComparison.OrdinalIgnoreCase);

    private static MagiskModuleInstallResult Fail(string file, string id, string name, string message) =>
        new()
        {
            Success = false,
            FilePath = file,
            ModuleId = id,
            ModuleName = name,
            Message = message
        };

    private static string Truncate(string text) =>
        text.Length <= 800 ? text : text[..800];
}
