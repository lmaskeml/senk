using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;

namespace AndroidManager.Core.Services;

/// <summary>
/// Xiaomi fastbootd vendor resize "locked" olduğunda: geçici TWRP boot + dd ile vendor yazımı.
/// </summary>
internal static class TwrpVendorDdWriter
{
    public static async Task<(bool Success, string Message)> TryWriteAsync(
        FastbootFlashRunner fastboot,
        IAdbService adb,
        string fastbootSerial,
        string vendorImagePath,
        string twrpImagePath,
        string activeSlot,
        IProgress<FlashingProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(vendorImagePath) || !File.Exists(twrpImagePath))
            return (false, "vendor veya TWRP imajı yok.");

        if (!await EnsureBootloaderAsync(fastboot, fastbootSerial, progress, cancellationToken).ConfigureAwait(false))
            return (false, "Bootloader'a geçilemedi (TWRP dd).");

        Report(progress, 93, "TWRP geçici açılıyor (fastboot boot) — vendor dd…");
        var boot = await fastboot
            .BootImageAsync(fastbootSerial, twrpImagePath, progress, cancellationToken)
            .ConfigureAwait(false);
        if (!boot.Success)
            return (false, "fastboot boot TWRP başarısız:\n" + boot.Message);

        var adbDevice = await adb.WaitForAdbDeviceAsync(TimeSpan.FromSeconds(90), cancellationToken)
            .ConfigureAwait(false);
        var adbSerial = adbDevice?.Serial;
        if (string.IsNullOrWhiteSpace(adbSerial))
            return (false, "TWRP ADB görünmedi — USB/izin kontrol edin.");

        var remotePath = "/tmp/am_vendor_dfe.img";
        Report(progress, 94, "vendor imajı TWRP'ye gönderiliyor (adb push)…");
        var push = await RunAdbAsync(
                adb,
                adbSerial,
                $"push \"{vendorImagePath}\" {remotePath}",
                TimeSpan.FromMinutes(10),
                cancellationToken)
            .ConfigureAwait(false);
        if (push.ExitCode != 0)
            return (false, "adb push vendor başarısız:\n" + push.Output);

        var slotSuffix = activeSlot is "a" or "b" ? $"_{activeSlot}" : "_a";
        var blockCandidates = new[]
        {
            $"/dev/block/by-name/vendor{slotSuffix}",
            $"/dev/block/bootdevice/by-name/vendor{slotSuffix}",
            $"/dev/block/mapper/vendor{slotSuffix}"
        };

        Report(progress, 95, "vendor dd yazılıyor (TWRP)…");
        _ = await RunAdbShellAsync(adb, adbSerial, "umount /vendor 2>/dev/null; true", cancellationToken)
            .ConfigureAwait(false);

        string? usedBlock = null;
        foreach (var block in blockCandidates)
        {
            var exists = await RunAdbShellAsync(
                    adb,
                    adbSerial,
                    $"[ -e {block} ] && echo OK || echo NO",
                    cancellationToken)
                .ConfigureAwait(false);
            if (!exists.Output.Contains("OK", StringComparison.Ordinal))
                continue;

            usedBlock = block;
            break;
        }

        if (usedBlock is null)
        {
            var find = await RunAdbShellAsync(
                    adb,
                    adbSerial,
                    "find /dev/block -name 'vendor" + slotSuffix + "' 2>/dev/null | head -1",
                    cancellationToken)
                .ConfigureAwait(false);
            usedBlock = find.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("/dev/", StringComparison.Ordinal));
        }

        if (string.IsNullOrWhiteSpace(usedBlock))
            return (false, $"vendor{slotSuffix} block cihazı bulunamadı.");

        var dd = await RunAdbShellAsync(
                adb,
                adbSerial,
                $"dd if={remotePath} of={usedBlock} bs=8M conv=fsync && echo DD_OK || echo DD_FAIL",
                cancellationToken)
            .ConfigureAwait(false);

        _ = await RunAdbShellAsync(adb, adbSerial, $"rm -f {remotePath}", cancellationToken).ConfigureAwait(false);

        if (!dd.Output.Contains("DD_OK", StringComparison.Ordinal))
            return (false, $"dd başarısız ({usedBlock}):\n{dd.Output}");

        Report(progress, 96, $"✅ vendor dd tamam ({usedBlock}) — bootloader'a dönülüyor…");
        _ = await adb.RebootTransportAsync("bootloader", adbSerial, cancellationToken).ConfigureAwait(false);
        await Task.Delay(8000, cancellationToken).ConfigureAwait(false);

        return (true, fastbootSerial);
    }

    private static async Task<bool> EnsureBootloaderAsync(
        FastbootFlashRunner fastboot,
        string serial,
        IProgress<FlashingProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        if (await fastboot.IsBootloaderFastbootAsync(serial, cancellationToken).ConfigureAwait(false))
            return true;

        if (await fastboot.IsFastbootdAsync(serial, cancellationToken).ConfigureAwait(false))
        {
            _ = await fastboot.RebootBootloaderAsync(serial, progress, cancellationToken).ConfigureAwait(false);
            await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (await fastboot.IsBootloaderFastbootAsync(serial, cancellationToken).ConfigureAwait(false))
                return true;
            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static Task<(int ExitCode, string Output)> RunAdbAsync(
        IAdbService adb,
        string serial,
        string argsAfterSerial,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        adb.RunHostAdbAsync(serial, argsAfterSerial, timeout, cancellationToken);

    private static Task<(int ExitCode, string Output)> RunAdbShellAsync(
        IAdbService adb,
        string serial,
        string shellCommand,
        CancellationToken cancellationToken)
    {
        var escaped = shellCommand.Replace("\"", "\\\"");
        return RunAdbAsync(adb, serial, $"shell \"{escaped}\"", TimeSpan.FromMinutes(10), cancellationToken);
    }

    private static void Report(IProgress<FlashingProgressReport>? progress, int percent, string message) =>
        progress?.Report(new FlashingProgressReport
        {
            Stage = CustomRomFlashStage.Flashing,
            Percent = percent,
            Message = message
        });
}
