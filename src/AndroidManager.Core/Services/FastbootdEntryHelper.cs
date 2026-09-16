using AndroidManager.Core.Models;

namespace AndroidManager.Core.Services;

internal sealed record FastbootdEntryResult(
    bool Success,
    string? Message = null,
    bool DeviceInRecoveryWithoutFastboot = false)
{
    public static FastbootdEntryResult Ok() => new(true);

    public static FastbootdEntryResult Fail(string message, bool inRecovery = false) =>
        new(false, message, inRecovery);
}

/// <summary>
/// Bootloader → fastbootd geçişi. TWRP recovery fastbootd protokolünü desteklemez;
/// <c>is-userspace: yes</c> doğrulanmadan devam edilmez.
/// </summary>
internal static class FastbootdEntryHelper
{
    private static readonly string[] FastbootdRebootCommands =
    [
        "reboot fastboot",
        "reboot fastbootd",
        "oem reboot-fastboot"
    ];

    public static async Task<FastbootdEntryResult> TryEnterAsync(
        FastbootFlashRunner fastboot,
        string serial,
        string? recoveryImagePath,
        IProgress<FlashingProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        if (await fastboot.IsFastbootdAsync(serial, cancellationToken).ConfigureAwait(false))
            return FastbootdEntryResult.Ok();

        Report(progress, 53, "Bootloader fastboot'a dönülüyor (fastbootd öncesi)…");
        var atBootloader = await EnsureBootloaderFastbootAsync(
                fastboot, serial, progress, cancellationToken)
            .ConfigureAwait(false);
        if (!atBootloader)
        {
            return FastbootdEntryResult.Fail(
                BuildFailureMessage(
                    "Bootloader fastboot moduna geçilemedi.",
                    "Telefonu ses tuşları + güç ile fastboot ekranına alın veya TWRP'de ADB açıksa 'adb reboot bootloader' deneyin."),
                inRecovery: true);
        }

        var unlockedRaw = await fastboot.GetVarAsync(serial, "unlocked", cancellationToken).ConfigureAwait(false);
        if (!ParseGetVarValue(unlockedRaw).Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            Report(progress, 53,
                "Uyarı: unlocked ≠ yes — fastbootd yine de denenecek…");
        }

        if (await TryRebootCommandsAsync(fastboot, serial, progress, cancellationToken).ConfigureAwait(false))
            return FastbootdEntryResult.Ok();

        if (!string.IsNullOrWhiteSpace(recoveryImagePath)
            && File.Exists(recoveryImagePath))
        {
            Report(progress, 53,
                "Fastbootd açılmadı — TWRP olabilir. ROM recovery yazılıp tekrar denenecek…");

            var flashRecovery = await fastboot
                .FlashPartitionAsync(serial, "recovery", recoveryImagePath, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!flashRecovery.Success)
            {
                return FastbootdEntryResult.Fail(
                    BuildFailureMessage(
                        "recovery.img yazılamadı.",
                        flashRecovery.Message),
                    inRecovery: true);
            }

            await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
            if (await TryRebootCommandsAsync(fastboot, serial, progress, cancellationToken).ConfigureAwait(false))
                return FastbootdEntryResult.Ok();
        }

        return FastbootdEntryResult.Fail(BuildTwrpVsFastbootdMessage(), inRecovery: true);
    }

    private static async Task<bool> TryRebootCommandsAsync(
        FastbootFlashRunner fastboot,
        string serial,
        IProgress<FlashingProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var command in FastbootdRebootCommands)
        {
            Report(progress, 54, $"Fastbootd deneniyor: fastboot {command}…");
            var reboot = await fastboot
                .RebootToFastbootdAsync(serial, command, progress, cancellationToken)
                .ConfigureAwait(false);

            if (IsUserspaceUnbootableError(reboot.Message))
            {
                Report(progress, 54,
                    "userspace fastboot unbootable — vbmeta/boot zinciri bozuk olabilir, bootloader'a dönülüyor…");
                _ = await fastboot.RebootBootloaderAsync(serial, progress, cancellationToken).ConfigureAwait(false);
                await Task.Delay(4000, cancellationToken).ConfigureAwait(false);
                await WaitForFastbootDeviceAsync(fastboot, serial, TimeSpan.FromSeconds(60), cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            await Task.Delay(5000, cancellationToken).ConfigureAwait(false);

            if (!await WaitForFastbootDeviceAsync(fastboot, serial, TimeSpan.FromSeconds(90), cancellationToken)
                    .ConfigureAwait(false))
            {
                _ = await TryAdbRebootBootloaderAsync(serial, cancellationToken).ConfigureAwait(false);
                await Task.Delay(4000, cancellationToken).ConfigureAwait(false);
                if (!await WaitForFastbootDeviceAsync(fastboot, serial, TimeSpan.FromSeconds(60), cancellationToken)
                        .ConfigureAwait(false))
                    continue;
            }

            if (await WaitForFastbootdAsync(fastboot, serial, TimeSpan.FromSeconds(45), cancellationToken)
                    .ConfigureAwait(false))
                return true;

            Report(progress, 54,
                $"Fastbootd doğrulanamadı (is-userspace ≠ yes) — sonraki komut deneniyor…");

            _ = await fastboot.RebootBootloaderAsync(serial, progress, cancellationToken).ConfigureAwait(false);
            await Task.Delay(4000, cancellationToken).ConfigureAwait(false);
            await WaitForFastbootDeviceAsync(fastboot, serial, TimeSpan.FromSeconds(60), cancellationToken)
                .ConfigureAwait(false);
        }

        return false;
    }

    private static bool IsUserspaceUnbootableError(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && (message.Contains("userspace fastboot", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unbootable", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Failed to boot into userspace", StringComparison.OrdinalIgnoreCase));

    private static async Task<bool> EnsureBootloaderFastbootAsync(
        FastbootFlashRunner fastboot,
        string serial,
        IProgress<FlashingProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        if (await fastboot.IsFastbootdAsync(serial, cancellationToken).ConfigureAwait(false))
        {
            _ = await fastboot.RebootBootloaderAsync(serial, progress, cancellationToken).ConfigureAwait(false);
            await Task.Delay(4000, cancellationToken).ConfigureAwait(false);
            return await WaitForBootloaderFastbootAsync(fastboot, serial, TimeSpan.FromSeconds(90), cancellationToken)
                .ConfigureAwait(false);
        }

        if (await WaitForFastbootDeviceAsync(fastboot, serial, TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false))
        {
            if (await fastboot.IsBootloaderFastbootAsync(serial, cancellationToken).ConfigureAwait(false))
                return true;

            _ = await fastboot.RebootBootloaderAsync(serial, progress, cancellationToken).ConfigureAwait(false);
            await Task.Delay(4000, cancellationToken).ConfigureAwait(false);
            return await WaitForBootloaderFastbootAsync(fastboot, serial, TimeSpan.FromSeconds(90), cancellationToken)
                .ConfigureAwait(false);
        }

        Report(progress, 53, "Fastboot bağlantısı yok — recovery/TWRP'den bootloader deneniyor (adb)…");
        _ = await TryAdbRebootBootloaderAsync(serial, cancellationToken).ConfigureAwait(false);
        await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
        return await WaitForBootloaderFastbootAsync(fastboot, serial, TimeSpan.FromSeconds(90), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<bool> WaitForBootloaderFastbootAsync(
        FastbootFlashRunner fastboot,
        string serial,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await fastboot.IsBootloaderFastbootAsync(serial, cancellationToken).ConfigureAwait(false))
                return true;
            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<bool> WaitForFastbootDeviceAsync(
        FastbootFlashRunner fastboot,
        string serial,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var devices = await fastboot.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
            if (devices.Any(d => d.Equals(serial, StringComparison.OrdinalIgnoreCase)))
                return true;
            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<bool> WaitForFastbootdAsync(
        FastbootFlashRunner fastboot,
        string serial,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await fastboot.IsFastbootdAsync(serial, cancellationToken).ConfigureAwait(false))
                return true;
            await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<bool> TryAdbRebootBootloaderAsync(
        string serial,
        CancellationToken cancellationToken)
    {
        var adb = PlatformToolsPathResolver.ResolveAdbPath();
        if (string.IsNullOrWhiteSpace(adb) || (!File.Exists(adb) && adb != "adb"))
            return false;

        return await DeviceTransportGate.RunAsync(async ct =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = adb,
                    Arguments = $"-s {serial} reboot bootloader",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var process = System.Diagnostics.Process.Start(psi);
                if (process is null)
                    return false;
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildTwrpVsFastbootdMessage() =>
        BuildFailureMessage(
            "Fastbootd moduna geçilemedi — cihaz muhtemelen TWRP recovery'de açıldı.",
            "TWRP ≠ fastbootd:\n" +
            "• TWRP: dokunmatik menü, adb sideload\n" +
            "• Fastbootd: sadece yazı, ekranda FASTBOOT / fastbootd, is-userspace: yes\n\n" +
            "Seçenekler:\n" +
            "1) TWRP → Apply Update → ADB Sideload (crDroid.zip)\n" +
            "2) ROM recovery.img varsa flash sonrası tekrar dene\n" +
            "3) Bootloader'da: fastboot reboot bootloader → fastboot reboot fastboot\n" +
            "4) TWRP yerine crDroid/stock recovery flash'la");

    private static string BuildFailureMessage(string headline, string detail) =>
        headline + "\n\n" + detail;

    private static string ParseGetVarValue(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return string.Empty;

        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            return line[(colon + 1)..].Trim();
        }

        return output.Trim();
    }

    private static void Report(IProgress<FlashingProgressReport>? progress, int percent, string message) =>
        progress?.Report(new FlashingProgressReport
        {
            Stage = CustomRomFlashStage.EnteringFastbootd,
            Percent = percent,
            Message = message
        });
}
