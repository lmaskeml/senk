using AndroidManager.Core.Models;

namespace AndroidManager.Core.Services;

/// <summary>
/// Bootloader'da vbmeta — stock flash_all.bat ile aynı:
/// <c>fastboot flash vbmeta_ab img --disable-verity --disable-verification</c>.
/// CLI AVB_MAGIC verirse flags image içine yazılır.
/// </summary>
internal static class BootloaderVbmetaFlasher
{
    private static readonly string[] VbmetaPartitions = ["vbmeta", "vbmeta_system"];

    public static async Task<(bool Success, string Message)> FlashDisableVerityAsync(
        FastbootFlashRunner fastboot,
        string serial,
        string workDir,
        string? imagesDir,
        IProgress<FlashingProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (await fastboot.IsFastbootdAsync(serial, cancellationToken).ConfigureAwait(false))
        {
            Report(progress, 88, "fastbootd → bootloader (vbmeta yazımı)…");
            _ = await fastboot.RebootBootloaderAsync(serial, progress, cancellationToken).ConfigureAwait(false);
            await Task.Delay(6000, cancellationToken).ConfigureAwait(false);
        }

        if (!await fastboot.IsBootloaderFastbootAsync(serial, cancellationToken).ConfigureAwait(false))
        {
            return (false,
                "Bootloader fastboot moduna geçilemedi.\n\n" +
                "vbmeta yazılmadan fastbootd'de vendor resize çalışmaz.");
        }

        var activeSlot = await ResolveActiveSlotAsync(fastboot, serial, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(workDir);

        foreach (var partition in VbmetaPartitions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var romPath = ResolveRomImagePath(imagesDir, partition);
            var stubPath = await VbmetaImageHelper
                .WriteDisabledStubAsync(workDir, partition, cancellationToken)
                .ConfigureAwait(false);
            var romDisabledCopy = string.IsNullOrWhiteSpace(romPath)
                ? null
                : await VbmetaImageHelper
                    .WriteDisabledRomCopyAsync(romPath, workDir, partition, cancellationToken)
                    .ConfigureAwait(false);
            var romHasAvb = romPath is not null && AvbImageHelper.HasAvbMagic(romPath);
            var hasRomImage = romPath is not null && new FileInfo(romPath).Length > 256;

            Report(progress, 89,
                hasRomImage
                    ? $"{partition}: dijital imza kapatılıyor (flash {partition}_ab --disable-verity)…"
                    : $"{partition} stub yazılıyor (ROM vbmeta yok, flags=disabled)…");

            var targets = XiaomiAbFlashProfile.GetFlashTargetCandidates(partition, activeSlot);
            FastbootCommandResult? last = null;
            string? successfulTarget = null;
            string flashPath = romDisabledCopy ?? (hasRomImage ? romPath! : stubPath);
            var usedDisableFlags = false;

            foreach (var target in targets)
            {
                foreach (var attempt in VbmetaImageHelper.EnumerateFlashAttempts(
                             romDisabledCopy, romPath, stubPath, romHasAvb))
                {
                    flashPath = attempt.ImagePath;
                    usedDisableFlags = attempt.DisableFlags;

                    last = attempt.GlobalFlags
                        ? await fastboot.FlashPartitionAsync(
                                serial, target, flashPath,
                                disableAvbFlags: true, progress, cancellationToken, globalAvbFlags: true)
                            .ConfigureAwait(false)
                        : await fastboot.FlashPartitionAsync(
                                serial, target, flashPath, usedDisableFlags, progress, cancellationToken)
                            .ConfigureAwait(false);

                    if (last.Success)
                    {
                        successfulTarget = target;
                        break;
                    }

                    if (attempt.DisableFlags && AvbImageHelper.IsAvbMagicError(last.Message))
                        continue;
                }

                if (successfulTarget is not null)
                    break;
            }

            if (successfulTarget is null)
            {
                return (false,
                    $"{partition} yazılamadı:\n{last?.Message ?? "bilinmeyen"}\n\n" +
                    "Bootloader açık (unlocked) olmalı.");
            }

            var mirror = XiaomiAbFlashProfile.GetMirrorSlotTarget(successfulTarget);
            if (mirror is not null)
            {
                _ = await fastboot
                    .FlashPartitionAsync(serial, mirror, flashPath, usedDisableFlags, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return (true, "vbmeta yazıldı.");
    }

    private static string? ResolveRomImagePath(string? imagesDir, string partition)
    {
        if (string.IsNullOrWhiteSpace(imagesDir))
            return null;

        var path = Path.Combine(imagesDir, $"{partition}.img");
        return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
    }

    private static async Task<string> ResolveActiveSlotAsync(
        FastbootFlashRunner fastboot,
        string serial,
        CancellationToken cancellationToken)
    {
        var raw = await fastboot.GetVarAsync(serial, "current-slot", cancellationToken).ConfigureAwait(false);
        var slot = ParseGetVarValue(raw).Trim().ToLowerInvariant();
        return slot is "a" or "b" ? slot : "a";
    }

    private static string ParseGetVarValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        foreach (var line in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
                continue;

            return trimmed[(colon + 1)..].Trim();
        }

        return raw.Trim();
    }

    private static void Report(IProgress<FlashingProgressReport>? progress, int percent, string message) =>
        progress?.Report(new FlashingProgressReport
        {
            Stage = CustomRomFlashStage.Flashing,
            Percent = percent,
            Message = message
        });
}
