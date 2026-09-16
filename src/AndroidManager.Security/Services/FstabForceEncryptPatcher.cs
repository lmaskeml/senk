using AndroidManager.Core;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Core.Services;
using Serilog;

namespace AndroidManager.Security.Services;

public sealed record FstabPatchResult(
    bool Success,
    string Message,
    IReadOnlyList<string> PatchedPaths);

/// <summary>
/// TWRP/recovery shell üzerinden fstab.qcom içinde forceencrypt → encryptable (DFE zip yerine kontrollü yol).
/// </summary>
internal static class FstabForceEncryptPatcher
{
    private static readonly string[] KnownFstabPaths =
    [
        "/vendor/etc/fstab.qcom",
        "/vendor/etc/fstab.vili",
        "/vendor/etc/fstab.qcom.vili",
        "/vendor/etc/fstab.default",
        "/system/vendor/etc/fstab.qcom",
        "/system/etc/fstab.qcom",
        "/system_root/vendor/etc/fstab.qcom",
    ];

    public static string PatchContent(string content) => FstabContentPatcher.PatchContent(content);

    public static bool NeedsPatch(string content) => FstabContentPatcher.NeedsPatch(content);

    public static async Task<FstabPatchResult> PatchViaRecoveryShellAsync(
        IAdbService adb,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        await TryMountWritableAsync(adb, logger, cancellationToken).ConfigureAwait(false);

        var allFstabs = await DiscoverAllFstabPathsAsync(adb, cancellationToken).ConfigureAwait(false);
        if (allFstabs.Count == 0)
        {
            return new FstabPatchResult(
                false,
                "fstab dosyası bulunamadı. TWRP'de System + Vendor mount edin ve tekrar deneyin.",
                []);
        }

        var candidates = new List<string>();
        foreach (var path in allFstabs)
        {
            var grep = await SafeShellAsync(
                    adb,
                    $"grep -E 'forceencrypt|fileencryption=|metadata_encryption=' \"{path}\" 2>/dev/null && echo HIT",
                    cancellationToken)
                .ConfigureAwait(false);
            if (grep.Contains("HIT", StringComparison.Ordinal))
                candidates.Add(path);
        }

        if (candidates.Count == 0)
        {
            return new FstabPatchResult(
                true,
                "✅ Bu ROM'da DFE bayrağı yok — fstab zaten şifresiz.",
                allFstabs);
        }

        var patched = new List<string>();
        var skipped = new List<string>();
        var failures = new List<string>();

        foreach (var path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var content = await SafeShellAsync(adb, $"cat \"{path}\" 2>/dev/null", cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
            {
                failures.Add($"{path}: okunamadı");
                continue;
            }

            if (!NeedsPatch(content))
            {
                skipped.Add(path);
                continue;
            }

            var sedOk = await TrySedPatchAsync(adb, path, cancellationToken).ConfigureAwait(false);
            if (!sedOk)
            {
                var pushed = await TryPushPatchAsync(adb, path, content, logger, cancellationToken)
                    .ConfigureAwait(false);
                if (!pushed)
                {
                    failures.Add($"{path}: sed/push başarısız");
                    continue;
                }
            }

            var verify = await SafeShellAsync(adb, $"cat \"{path}\" 2>/dev/null", cancellationToken)
                .ConfigureAwait(false);
            if (NeedsPatch(verify))
            {
                failures.Add($"{path}: doğrulama — hâlâ forceencrypt var");
                continue;
            }

            await SafeShellAsync(adb, $"chmod 644 \"{path}\" 2>/dev/null", cancellationToken)
                .ConfigureAwait(false);
            patched.Add(path);
            logger.Information("[Fstab] Patched {Path}", path);
        }

        if (patched.Count > 0)
        {
            var summary = string.Join(Environment.NewLine, patched.Select(p => "  • " + p));
            var extra = skipped.Count > 0
                ? $"{Environment.NewLine}Zaten temiz: {string.Join(", ", skipped)}"
                : "";
            return new FstabPatchResult(
                true,
                $"✅ Güvenlik ayarları düzenlendi ({patched.Count} dosya):{Environment.NewLine}{summary}{extra}",
                patched);
        }

        if (skipped.Count > 0 && failures.Count == 0)
        {
            return new FstabPatchResult(
                true,
                "Fstab dosyalarında forceencrypt bulunamadı (zaten encryptable veya ROM FBE kullanıyor).",
                skipped);
        }

        return new FstabPatchResult(
            false,
            "⚠️ Şifreleme kaldırılamadı — TWRP'de System/Vendor mount edip elle deneyin." +
            (failures.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, failures) : ""),
            []);
    }

    private static async Task TryMountWritableAsync(IAdbService adb, ILogger logger, CancellationToken ct)
    {
        foreach (var cmd in new[]
                 {
                     "twrp mount system",
                     "twrp mount vendor",
                     "mount /system",
                     "mount /vendor",
                     "mount -o rw,remount /system_root",
                     "mount -o rw,remount /system",
                     "mount -o rw,remount /vendor",
                     "mount -o rw,remount /",
                 })
        {
            var output = await SafeShellAsync(adb, cmd, ct).ConfigureAwait(false);
            logger.Debug("[Fstab] {Cmd} => {Out}", cmd, Truncate(output, 120));
        }
    }

    private static async Task<IReadOnlyList<string>> DiscoverAllFstabPathsAsync(
        IAdbService adb,
        CancellationToken ct)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);

        var findCmd =
            "find /vendor /system /system_root -maxdepth 6 -type f \\( -name 'fstab*.qcom' -o -name 'fstab.default' \\) 2>/dev/null";
        var findOut = await SafeShellWithTimeoutAsync(adb, findCmd, TimeSpan.FromSeconds(20), ct)
            .ConfigureAwait(false);
        foreach (var line in SplitLines(findOut))
        {
            if (line.StartsWith('/'))
                found.Add(line.Trim());
        }

        foreach (var known in KnownFstabPaths)
        {
            var probe = await SafeShellAsync(adb, $"test -f \"{known}\" && echo OK", ct).ConfigureAwait(false);
            if (probe.Contains("OK", StringComparison.Ordinal))
                found.Add(known);
        }

        return found.OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    private static async Task<bool> TrySedPatchAsync(IAdbService adb, string path, CancellationToken ct)
    {
        await SafeShellAsync(adb, $"sed -i 's/forceencrypt/encryptable/g' \"{path}\" 2>/dev/null", ct)
            .ConfigureAwait(false);
        var verify = await SafeShellAsync(adb, $"cat \"{path}\" 2>/dev/null", ct).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(verify) && !NeedsPatch(verify);
    }

    private static async Task<bool> TryPushPatchAsync(
        IAdbService adb,
        string remotePath,
        string originalContent,
        ILogger logger,
        CancellationToken ct)
    {
        var patched = PatchContent(originalContent);
        var tempLocal = Path.Combine(Path.GetTempPath(), $"am_fstab_{Guid.NewGuid():N}.qcom");
        var remoteTmp = $"/tmp/am_fstab_{Guid.NewGuid():N}.qcom";
        try
        {
            await File.WriteAllTextAsync(tempLocal, patched, ct).ConfigureAwait(false);
            if (!await AdbPushAsync(adb, tempLocal, remoteTmp, ct).ConfigureAwait(false))
                return false;

            var copy = await SafeShellAsync(
                adb,
                $"cat \"{remoteTmp}\" > \"{remotePath}\" 2>/dev/null && rm -f \"{remoteTmp}\"",
                ct).ConfigureAwait(false);
            logger.Debug("[Fstab] push fallback {Path}: {Out}", remotePath, Truncate(copy, 80));
            return true;
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "[Fstab] push fallback failed for {Path}", remotePath);
            return false;
        }
        finally
        {
            try { File.Delete(tempLocal); } catch { /* ignore */ }
        }
    }

    private static async Task<string> SafeShellAsync(IAdbService adb, string command, CancellationToken ct) =>
        await SafeShellWithTimeoutAsync(adb, command, TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);

    private static async Task<string> SafeShellWithTimeoutAsync(
        IAdbService adb,
        string command,
        TimeSpan timeout,
        CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            return await adb.ExecuteShellAsync(command, timeoutCts.Token).ConfigureAwait(false);
        }
        catch
        {
            return "";
        }
    }

    private static async Task<bool> AdbPushAsync(
        IAdbService adb,
        string localPath,
        string remotePath,
        CancellationToken ct)
    {
        var serial = adb.SelectedDevice?.Serial;
        var (exitCode, _) = await adb.RunHostAdbAsync(
            serial,
            $"push \"{localPath}\" \"{remotePath}\"",
            TimeSpan.FromSeconds(45),
            ct).ConfigureAwait(false);
        return exitCode == 0;
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
