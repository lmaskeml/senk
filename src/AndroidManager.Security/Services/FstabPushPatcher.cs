using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Security.Services;

/// <summary>PC'de düzenlenmiş fstab dosyalarını TWRP'ye adb push ile yazar (yalnızca yazılabilir ext4 vendor).</summary>
internal static class FstabPushPatcher
{
    private static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan VendorWriteProbeTimeout = TimeSpan.FromSeconds(12);

    public static async Task<FstabPatchResult> PushPreparedFilesAsync(
        IAdbService adb,
        IReadOnlyList<PatchedFstabFile> files,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (files.Count == 0)
        {
            return new FstabPatchResult(
                true,
                "Push edilecek fstab dosyası yok.",
                []);
        }

        if (!await IsVendorWritableAsync(adb, logger, cancellationToken).ConfigureAwait(false))
        {
            return new FstabPatchResult(
                false,
                "⚠️ /vendor salt okunur (Android 15 EROFS). adb push desteklenmiyor.\n\n" +
                "PC'de mkfs.erofs ile vendor hazırlanıp fastbootd flash kullanılmalı.",
                []);
        }

        await TryMountVendorAsync(adb, logger, cancellationToken).ConfigureAwait(false);

        var pushed = new List<string>();
        var failures = new List<string>();
        var serial = adb.SelectedDevice?.Serial;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(file.LocalPath))
            {
                failures.Add($"{file.DevicePath}: PC dosyası bulunamadı");
                continue;
            }

            if (!await AdbPushAsync(adb, file.LocalPath, file.DevicePath, serial, cancellationToken)
                    .ConfigureAwait(false))
            {
                failures.Add($"{file.DevicePath}: adb push başarısız veya zaman aşımı");
                continue;
            }

            await SafeShellAsync(adb, $"chmod 644 \"{file.DevicePath}\"", cancellationToken)
                .ConfigureAwait(false);

            var verify = await SafeShellAsync(
                adb,
                $"grep -E 'forceencrypt|fileencryption=' \"{file.DevicePath}\" 2>/dev/null && echo BAD || echo OK",
                cancellationToken).ConfigureAwait(false);

            if (verify.Contains("BAD", StringComparison.Ordinal))
            {
                failures.Add($"{file.DevicePath}: push sonrası hâlâ şifreleme bayrağı var");
                continue;
            }

            pushed.Add(file.DevicePath);
            logger.Information("[Fstab] Pushed {Path}", file.DevicePath);
        }

        if (pushed.Count > 0)
        {
            var summary = string.Join(Environment.NewLine, pushed.Select(p => "  • " + p));
            return new FstabPatchResult(
                true,
                $"✅ Güvenlik ayarları yüklendi ({pushed.Count} dosya):{Environment.NewLine}{summary}",
                pushed);
        }

        return new FstabPatchResult(
            false,
            "⚠️ fstab push başarısız." +
            (failures.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, failures) : ""),
            []);
    }

    private static async Task<bool> IsVendorWritableAsync(
        IAdbService adb,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(VendorWriteProbeTimeout);

        try
        {
            var probe = await SafeShellAsync(
                    adb,
                    "touch /vendor/etc/.am_write_test 2>&1; echo AM_PROBE:$?",
                    timeoutCts.Token)
                .ConfigureAwait(false);

            _ = await SafeShellAsync(
                    adb,
                    "rm -f /vendor/etc/.am_write_test 2>/dev/null",
                    timeoutCts.Token)
                .ConfigureAwait(false);

            var writable = probe.Contains("AM_PROBE:0", StringComparison.Ordinal);
            if (!writable)
                logger.Warning("[Fstab] /vendor yazılabilir değil: {Probe}", Truncate(probe, 120));

            return writable;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.Warning("[Fstab] /vendor yazılabilirlik testi zaman aşımı — EROFS olabilir");
            return false;
        }
    }

    private static async Task TryMountVendorAsync(IAdbService adb, ILogger logger, CancellationToken ct)
    {
        foreach (var cmd in new[]
                 {
                     "twrp mount vendor",
                     "mount /vendor",
                     "mount -o rw,remount /vendor",
                 })
        {
            var output = await SafeShellAsync(adb, cmd, ct).ConfigureAwait(false);
            logger.Debug("[Fstab] {Cmd} => {Out}", cmd, Truncate(output, 100));
        }
    }

    private static async Task<bool> AdbPushAsync(
        IAdbService adb,
        string localPath,
        string remotePath,
        string? serial,
        CancellationToken cancellationToken)
    {
        var (exitCode, _) = await adb.RunHostAdbAsync(
            serial,
            $"push \"{localPath}\" \"{remotePath}\"",
            PushTimeout,
            cancellationToken).ConfigureAwait(false);
        return exitCode == 0;
    }

    private static async Task<string> SafeShellAsync(IAdbService adb, string command, CancellationToken ct)
    {
        try
        {
            return await adb.ExecuteShellAsync(command, ct).ConfigureAwait(false);
        }
        catch
        {
            return "";
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
