using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Apps.Services;

public sealed partial class AppService : IAppService
{
    private readonly IAdbService _adb;
    private readonly IAdbSyncService _sync;
    private readonly ILogger _logger;
    private readonly string _aaptPath;

    public AppService(IAdbService adb, IAdbSyncService sync, ILogger? logger = null)
    {
        _adb = adb;
        _sync = sync;
        _logger = logger ?? Log.ForContext<AppService>();
        _aaptPath = ResolveAaptPath();
    }

    public async Task<IReadOnlyList<AndroidApp>> GetInstalledAppsAsync(
        bool includeSystem = false,
        CancellationToken cancellationToken = default)
    {
        var flag = includeSystem ? string.Empty : "-3";
        var raw = await _adb.ExecuteShellAsync($"pm list packages {flag} -f", cancellationToken)
            .ConfigureAwait(false);

        var apps = new List<AndroidApp>(capacity: 256);
        foreach (Match match in PackageLineRegex().Matches(raw))
        {
            var apkPath = match.Groups["apk"].Value.Trim();
            var pkg = match.Groups["pkg"].Value.Trim();
            apps.Add(new AndroidApp
            {
                PackageName = pkg,
                ApkPath = apkPath,
                IsSystemApp = apkPath.StartsWith("/system", StringComparison.OrdinalIgnoreCase)
                              || apkPath.StartsWith("/product", StringComparison.OrdinalIgnoreCase)
                              || apkPath.StartsWith("/vendor", StringComparison.OrdinalIgnoreCase)
            });
        }

        // Bounded concurrency — tek ADB kanalında kuyruk oluşmasını sınırla.
        foreach (var batch in apps.Chunk(3))
            await Task.WhenAll(batch.Select(a => FillMetadataAsync(a, cancellationToken))).ConfigureAwait(false);

        apps.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.AppName, b.AppName));
        return apps;
    }

    public async Task<InstallResult> InstallApkAsync(
        string localApkPath,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(localApkPath))
                return new InstallResult { Success = false, Message = "Dosya bulunamadı." };

            if (AndroidPackageFormats.IsSplitArchive(localApkPath))
                return await InstallSplitPackageAsync(localApkPath, progress, cancellationToken)
                    .ConfigureAwait(false);

            progress?.Report(10);
            // Prefer host-side adb install (faster / more reliable than push+pm).
            var result = await _adb.InstallLocalPackagesAsync([localApkPath], cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(100);
            return result;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "APK install failed: {Path}", localApkPath);
            return new InstallResult { Success = false, Message = ex.Message };
        }
    }

    private async Task<InstallResult> InstallSplitPackageAsync(
        string archivePath,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        SplitPackageContents? contents = null;
        try
        {
            progress?.Report(5);
            contents = SplitPackageExtractor.Extract(archivePath);
            _logger.Information(
                "Split package {Path}: {ApkCount} APK, {ObbCount} OBB",
                archivePath,
                contents.ApkPaths.Count,
                contents.ObbFiles.Count);

            progress?.Report(25);
            var install = await _adb.InstallLocalPackagesAsync(contents.ApkPaths, cancellationToken)
                .ConfigureAwait(false);
            if (!install.Success)
                return install;

            progress?.Report(70);
            if (contents.ObbFiles.Count > 0)
            {
                var obbMsg = await PushObbFilesAsync(contents, cancellationToken).ConfigureAwait(false);
                progress?.Report(100);
                return new InstallResult
                {
                    Success = true,
                    Message = string.IsNullOrWhiteSpace(obbMsg)
                        ? $"{install.Message.Trim()} ({contents.ApkPaths.Count} split APK)"
                        : $"{install.Message.Trim()} | OBB: {obbMsg}"
                };
            }

            progress?.Report(100);
            return new InstallResult
            {
                Success = true,
                Message = $"{install.Message.Trim()} ({contents.ApkPaths.Count} split APK)"
            };
        }
        finally
        {
            SplitPackageExtractor.TryDeleteDirectory(contents?.ExtractDirectory);
        }
    }

    private async Task<string> PushObbFilesAsync(
        SplitPackageContents contents,
        CancellationToken cancellationToken)
    {
        var pushed = 0;
        var errors = new List<string>();

        foreach (var obb in contents.ObbFiles)
        {
            try
            {
                string remote;
                if (!string.IsNullOrWhiteSpace(obb.RelativeRemotePath))
                {
                    remote = $"/sdcard/Android/obb/{obb.RelativeRemotePath.TrimStart('/')}";
                }
                else if (!string.IsNullOrWhiteSpace(contents.PackageName))
                {
                    remote = $"/sdcard/Android/obb/{contents.PackageName}/{obb.FileName}";
                }
                else
                {
                    errors.Add($"{obb.FileName} (paket adı yok)");
                    continue;
                }

                var remoteDir = remote.Replace('\\', '/');
                var lastSlash = remoteDir.LastIndexOf('/');
                if (lastSlash > 0)
                {
                    await _adb.ExecuteShellAsync($"mkdir -p \"{remoteDir[..lastSlash]}\"", cancellationToken)
                        .ConfigureAwait(false);
                }

                await _sync.PushAsync(obb.LocalPath, remote, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                pushed++;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "OBB push failed: {File}", obb.FileName);
                errors.Add(obb.FileName);
            }
        }

        if (errors.Count == 0)
            return $"{pushed} dosya kopyalandı";
        return $"{pushed} ok, hata: {string.Join(", ", errors)}";
    }

    public async Task<bool> UninstallAsync(string packageName, CancellationToken cancellationToken = default)
    {
        var result = await _adb.ExecuteShellAsync($"pm uninstall {packageName}", cancellationToken)
            .ConfigureAwait(false);
        return result.Contains("Success", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> ExtractApkAsync(
        string packageName,
        string saveDir,
        CancellationToken cancellationToken = default)
    {
        var apkPathRaw = await _adb.ExecuteShellAsync($"pm path {packageName}", cancellationToken)
            .ConfigureAwait(false);
        var apkPath = apkPathRaw.Replace("package:", "", StringComparison.OrdinalIgnoreCase).Trim()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? throw new InvalidOperationException("APK yolu bulunamadı.");

        Directory.CreateDirectory(saveDir);
        var destPath = Path.Combine(saveDir, $"{packageName}.apk");
        await _sync.PullAsync(apkPath, destPath, cancellationToken: cancellationToken).ConfigureAwait(false);
        return destPath;
    }

    public async Task<byte[]?> GetAppIconAsync(string packageName, CancellationToken cancellationToken = default)
    {
        string? tempApk = null;
        try
        {
            var apkPathRaw = await _adb.ExecuteShellAsync($"pm path {packageName}", cancellationToken)
                .ConfigureAwait(false);
            var apkPath = apkPathRaw.Replace("package:", "", StringComparison.OrdinalIgnoreCase).Trim()
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(apkPath))
                return null;

            tempApk = Path.Combine(Path.GetTempPath(), $"{packageName}_{Guid.NewGuid():N}.apk");
            await _sync.PullAsync(apkPath, tempApk, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (File.Exists(_aaptPath))
            {
                var iconEntry = await GetIconEntryViaAaptAsync(tempApk, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(iconEntry))
                {
                    var bytes = ExtractZipEntry(tempApk, iconEntry);
                    if (bytes is not null)
                        return bytes;
                }
            }

            return await ExtractIconFromApkManuallyAsync(tempApk).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Icon fetch failed: {Pkg}", packageName);
            return null;
        }
        finally
        {
            if (tempApk is not null && File.Exists(tempApk))
            {
                try { File.Delete(tempApk); } catch { /* ignore */ }
            }
        }
    }

    public Task ForceStopAsync(string packageName, CancellationToken cancellationToken = default) =>
        _adb.ExecuteShellAsync($"am force-stop {packageName}", cancellationToken);

    public Task ClearDataAsync(string packageName, CancellationToken cancellationToken = default) =>
        _adb.ExecuteShellAsync($"pm clear {packageName}", cancellationToken);

    public async Task<IReadOnlyList<DebloatCandidate>> GetDebloatCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        var installed = await GetInstalledAppsAsync(includeSystem: true, cancellationToken)
            .ConfigureAwait(false);
        var disabledRaw = await _adb.ExecuteShellAsync("pm list packages -d --user 0", cancellationToken)
            .ConfigureAwait(false);
        var disabled = new HashSet<string>(
            PackageOnlyRegex().Matches(disabledRaw).Select(m => m.Groups["pkg"].Value),
            StringComparer.OrdinalIgnoreCase);

        var list = new List<DebloatCandidate>();
        foreach (var app in installed)
        {
            if (DebloatCatalog.IsBlocked(app.PackageName))
                continue;

            var (category, reason, isKnown) = DebloatCatalog.Describe(app.PackageName, app.IsSystemApp);
            if (!isKnown && !app.IsSystemApp)
                continue;

            if (category == "Kritik")
                continue;

            list.Add(new DebloatCandidate
            {
                PackageName = app.PackageName,
                AppName = string.IsNullOrWhiteSpace(app.AppName) ? app.PackageName : app.AppName,
                Category = category,
                Reason = reason,
                IsKnownBloat = isKnown,
                IsDisabledForUser = disabled.Contains(app.PackageName),
                IsSystemApp = app.IsSystemApp,
                Risk = DebloatCatalog.GetRisk(category, isKnown)
            });
        }

        return list
            .OrderByDescending(c => c.IsKnownBloat)
            .ThenBy(c => c.Category)
            .ThenBy(c => c.AppName)
            .ToList();
    }

    public async Task<DebloatResult> DisableForUserAsync(
        string packageName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return new DebloatResult { Success = false, Message = "Paket adı boş." };

        if (DebloatCatalog.IsBlocked(packageName))
            return new DebloatResult { Success = false, Message = "Bu paket güvenlik nedeniyle engellendi." };

        var output = await _adb.ExecuteShellAsync(
                $"pm uninstall -k --user 0 {packageName}",
                cancellationToken)
            .ConfigureAwait(false);

        var ok = output.Contains("Success", StringComparison.OrdinalIgnoreCase);
        _logger.Information("Debloat disable {Pkg}: {Output}", packageName, output.Trim());
        return new DebloatResult
        {
            Success = ok,
            PackageName = packageName,
            Message = ok
                ? $"{packageName} kullanıcı 0 için kaldırıldı (veri korundu)."
                : $"Başarısız: {output.Trim()}"
        };
    }

    public async Task<DebloatResult> RestoreForUserAsync(
        string packageName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return new DebloatResult { Success = false, Message = "Paket adı boş." };

        var output = await _adb.ExecuteShellAsync(
                $"cmd package install-existing --user 0 {packageName}",
                cancellationToken)
            .ConfigureAwait(false);

        var ok = output.Contains("installed for user", StringComparison.OrdinalIgnoreCase)
                 || output.Contains("Package", StringComparison.OrdinalIgnoreCase)
                    && !output.Contains("Exception", StringComparison.OrdinalIgnoreCase)
                    && !output.Contains("Error", StringComparison.OrdinalIgnoreCase);

        // Some firmwares print only the package path on success.
        if (!ok && !string.IsNullOrWhiteSpace(output)
                && !output.Contains("Exception", StringComparison.OrdinalIgnoreCase)
                && !output.Contains("Error", StringComparison.OrdinalIgnoreCase)
                && !output.Contains("Unknown", StringComparison.OrdinalIgnoreCase))
            ok = true;

        _logger.Information("Debloat restore {Pkg}: {Output}", packageName, output.Trim());
        return new DebloatResult
        {
            Success = ok,
            PackageName = packageName,
            Message = ok
                ? $"{packageName} kullanıcı 0 için geri yüklendi."
                : $"Geri yükleme başarısız: {output.Trim()}"
        };
    }

    private async Task FillMetadataAsync(AndroidApp app, CancellationToken cancellationToken)
    {
        try
        {
            var dump = await _adb.ExecuteShellAsync(
                    $"dumpsys package {app.PackageName}",
                    cancellationToken)
                .ConfigureAwait(false);

            app.VersionName = ParseValue(dump, "versionName=");
            if (int.TryParse(ParseValue(dump, "versionCode=").Split([' ', '\r', '\n'])[0],
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var vc))
                app.VersionCode = vc;

            app.InstallDate = ParseInstallDate(dump);
            app.AppName = await GetLabelAsync(app.PackageName, dump, cancellationToken).ConfigureAwait(false);

            var sizeRaw = await _adb.ExecuteShellAsync($"stat -c %s \"{app.ApkPath}\" 2>/dev/null", cancellationToken)
                .ConfigureAwait(false);
            if (long.TryParse(sizeRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
                app.ApkSize = size;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Metadata failed: {Pkg}", app.PackageName);
            if (string.IsNullOrWhiteSpace(app.AppName))
                app.AppName = DeriveName(app.PackageName);
        }
    }

    private async Task<string> GetLabelAsync(string packageName, string dump, CancellationToken cancellationToken)
    {
        // dumpsys often has applicationLabel / labelRes; fallback to package tail.
        var label = ParseValue(dump, "applicationLabel=");
        if (!string.IsNullOrWhiteSpace(label))
            return label.Trim('\'', '"');

        // Try aapt if we already pulled nothing — keep cheap.
        _ = cancellationToken;
        return DeriveName(packageName);
    }

    private async Task<string?> GetIconEntryViaAaptAsync(string apkPath, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _aaptPath,
            Arguments = $"dump badging \"{apkPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc is null) return null;

        var output = await proc.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var matches = IconEntryRegex().Matches(output);
        return matches.Count > 0 ? matches[^1].Groups[1].Value : null;
    }

    private static byte[]? ExtractZipEntry(string zipPath, string entryName)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.FirstOrDefault(e =>
            e.FullName.Equals(entryName, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;

        using var ms = new MemoryStream();
        using var es = entry.Open();
        es.CopyTo(ms);
        return ms.ToArray();
    }

    private static async Task<byte[]?> ExtractIconFromApkManuallyAsync(string apkPath)
    {
        using var zip = ZipFile.OpenRead(apkPath);
        var iconEntry = zip.Entries
            .Where(e => e.FullName.Contains("ic_launcher", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Length)
            .FirstOrDefault();

        if (iconEntry is null) return null;

        await using var ms = new MemoryStream();
        await using var es = iconEntry.Open();
        await es.CopyToAsync(ms).ConfigureAwait(false);
        return ms.ToArray();
    }

    private static string DeriveName(string packageName)
    {
        var parts = packageName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0
            ? CultureInfo.CurrentCulture.TextInfo.ToTitleCase(parts[^1])
            : packageName;
    }

    private static string ParseValue(string text, string key)
    {
        var idx = text.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return string.Empty;
        var remainder = text[(idx + key.Length)..];
        return remainder.Split(['\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
    }

    private static DateTime ParseInstallDate(string dump)
    {
        var raw = ParseValue(dump, "firstInstallTime=");
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
            return dt;

        // Some devices: "firstInstallTime=2024-01-01 12:00:00"
        var lineIdx = dump.IndexOf("firstInstallTime=", StringComparison.Ordinal);
        if (lineIdx >= 0)
        {
            var line = dump[lineIdx..].Split('\n')[0];
            var value = line["firstInstallTime=".Length..].Trim();
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt))
                return dt;
        }

        return default;
    }

    private static string ResolveAaptPath()
    {
        var bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "aapt.exe");
        if (File.Exists(bundled)) return bundled;

        var repoTools = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "tools", "aapt", "aapt.exe");
        try
        {
            var full = Path.GetFullPath(repoTools);
            if (File.Exists(full)) return full;
        }
        catch { /* ignore */ }

        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Android", "Sdk", "build-tools");
        if (Directory.Exists(local))
        {
            var candidate = Directory.GetDirectories(local)
                .OrderByDescending(d => d)
                .Select(d => Path.Combine(d, "aapt.exe"))
                .FirstOrDefault(File.Exists);
            if (candidate is not null) return candidate;
        }

        return bundled;
    }

    [GeneratedRegex(@"package:(?<apk>.+?)=(?<pkg>[\w\.]+)", RegexOptions.Compiled)]
    private static partial Regex PackageLineRegex();

    [GeneratedRegex(@"package:(?<pkg>[\w\.]+)", RegexOptions.Compiled)]
    private static partial Regex PackageOnlyRegex();

    [GeneratedRegex(@"application-icon-\d+:'([^']+)'", RegexOptions.Compiled)]
    private static partial Regex IconEntryRegex();
}
