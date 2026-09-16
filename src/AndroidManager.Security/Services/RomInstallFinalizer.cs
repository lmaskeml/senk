using AndroidManager.Core;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Core.Services;
using Serilog;

namespace AndroidManager.Security.Services;

/// <summary>Sideload sonrası ROM boot imajlarını fastboot ile yazar — TWRP'de kalma riskini giderir.</summary>
internal sealed class RomInstallFinalizer
{
    private readonly RomBootImageResolver _bootResolver = new();
    private readonly PatchedVendorImageFlasher _vendorFlasher = new();

    public Task<RomBootImages?> PrepareBootImagesAsync(
        string zipPath,
        IProgress<FlashingProgressReport>? progress = null,
        CancellationToken cancellationToken = default) =>
        _bootResolver.ResolveAsync(zipPath, progress, cancellationToken);

    public async Task<DeviceToolResult> FinalizeAfterSideloadAsync(
        string zipPath,
        RomBootImages? preparedBootImages,
        IFastbootDiscoveryService fastboot,
        IAdbService adb,
        ILogger logger,
        string? patchedVendorImagePath = null,
        string? twrpImagePath = null,
        IRecoveryManagerService? recovery = null,
        IProgress<CustomRomInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Report(progress, CustomRomInstallStage.FinalizingBoot, 96, "Sistem dosyaları tamamlanıyor…");

        var bootImages = preparedBootImages
                         ?? await _bootResolver.ResolveAsync(
                             zipPath,
                             MapProgress(progress, 96, 98),
                             cancellationToken).ConfigureAwait(false);

        if (bootImages is null)
        {
            return Fail(
                "ROM'dan boot imajı çıkarılamadı.\n\n" +
                "Telefon TWRP'de kalabilir. Manuel: payload'dan boot.img çıkarıp fastboot flash boot yapın.");
        }

        var bootloader = await PostSideloadRecoveryHelper
            .RebootToBootloaderAsync(adb, fastboot, logger, progress, cancellationToken)
            .ConfigureAwait(false);
        if (!bootloader.Success)
            return Fail(bootloader.Message);

        var serial = bootloader.Message;
        if (string.IsNullOrWhiteSpace(serial) || serial.Contains('\n', StringComparison.Ordinal))
        {
            serial = await WaitForFastbootSerialAsync(fastboot, TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(serial))
        {
            return Fail(
                "Boot yazımı için fastboot cihazı bulunamadı.\n\n" +
                "TWRP → Reboot → Bootloader deneyin.");
        }

        // boot/vendor_boot ÖNCE — Xiaomi fastbootd userspace bu imajlardan yüklenir.
        var activeSlot = await ResolveActiveSlotAsync(fastboot, serial, cancellationToken).ConfigureAwait(false);
        logger.Information("[CustomROM] Finalize boot flash — slot {Slot}, serial {Serial}", activeSlot, serial);

        var flashOrder = new List<(string Partition, string Path)>();
        if (bootImages.VendorBootImagePath is not null)
            flashOrder.Add(("vendor_boot", bootImages.VendorBootImagePath));
        if (bootImages.InitBootImagePath is not null)
            flashOrder.Add(("init_boot", bootImages.InitBootImagePath));
        flashOrder.Add(("boot", bootImages.BootImagePath));

        foreach (var (partition, imagePath) in flashOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, CustomRomInstallStage.FinalizingBoot, 88,
                $"Sistem dosyası yazılıyor (fastbootd öncesi): {partition}…");

            var flashResult = await FlashPartitionWithMirrorAsync(
                    serial, partition, imagePath, activeSlot, logger, cancellationToken)
                .ConfigureAwait(false);
            if (!flashResult.Success)
                return flashResult;
        }

        Report(progress, CustomRomInstallStage.FinalizingBoot, 89, "✅ Boot imajları yazıldı");

        if (!string.IsNullOrWhiteSpace(patchedVendorImagePath))
        {
            Report(progress, CustomRomInstallStage.PatchingFstab, 90,
                "Güvenlik ayarları yazılıyor (fastbootd vendor)…");

            var vendorFlash = await _vendorFlasher
                .FlashInFastbootdAsync(
                    serial,
                    patchedVendorImagePath,
                    zipPath,
                    MapFlashProgress(progress, 90, 95),
                    cancellationToken,
                    twrpImagePath,
                    adb)
                .ConfigureAwait(false);
            if (!vendorFlash.Success)
                return Fail(vendorFlash.Message);

            var dfeSkipped = false;
            if (!string.IsNullOrWhiteSpace(vendorFlash.Message)
                && !vendorFlash.Message.Contains('\n', StringComparison.Ordinal))
            {
                if (vendorFlash.Message.StartsWith(
                        PatchedVendorImageFlasher.DfeSkippedPrefix, StringComparison.Ordinal))
                {
                    dfeSkipped = true;
                    serial = vendorFlash.Message[PatchedVendorImageFlasher.DfeSkippedPrefix.Length..];
                }
                else
                {
                    serial = vendorFlash.Message;
                }
            }
            else
            {
                var rediscovered = await WaitForFastbootSerialAsync(fastboot, TimeSpan.FromSeconds(60), cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(rediscovered))
                    serial = rediscovered;
            }

            Report(progress, CustomRomInstallStage.PatchingFstab, 95,
                dfeSkipped
                    ? "⚠️ DFE vendor atlandı — ROM kuruldu (şifreleme açık kalabilir)."
                    : "✅ vendor (DFE) yazıldı.");
        }

        Report(progress, CustomRomInstallStage.FinalizingBoot, 99, "✅ Sistem dosyaları tamamlandı");

        var twrpInstalled = false;
        if (!string.IsNullOrWhiteSpace(twrpImagePath) && File.Exists(twrpImagePath))
        {
            // vili vb. boot-as-recovery: ham «fastboot flash boot twrp.img» sistemi bozar
            // (her açılışta TWRP). Doğru yöntem: ROM boot + Install Recovery Ramdisk.
            Report(progress, CustomRomInstallStage.FlashingTwrp, 99,
                "Kalıcı TWRP: ROM boot korunuyor → Recovery Ramdisk (boot'a ham TWRP YAZILMAZ)…");

            var runner = new FastbootFlashRunner();
            var ramdisk = await XiaomiAbTwrpRamdiskInstaller
                .TryInstallAsync(
                    runner,
                    adb,
                    serial,
                    bootImages.BootImagePath,
                    twrpImagePath,
                    activeSlot,
                    MapFlashProgress(progress, 97, 99),
                    cancellationToken)
                .ConfigureAwait(false);

            logger.Information("[CustomROM] Xiaomi TWRP ramdisk install: ok={Ok} {Msg}",
                ramdisk.Success, ramdisk.Message);

            if (ramdisk.Success)
            {
                twrpInstalled = true;
                Report(progress, CustomRomInstallStage.FlashingTwrp, 99,
                    "✅ " + ramdisk.Message);
            }
            else
            {
                Report(progress, CustomRomInstallStage.FlashingTwrp, 99,
                    "⚠️ " + ramdisk.Message);
            }
        }

        if (twrpInstalled)
        {
            return new DeviceToolResult
            {
                Success = true,
                Message =
                    "Boot yazıldı, TWRP Recovery Ramdisk kuruldu — sistem açılıyor.\n" +
                    "TWRP için: kapalıyken Vol+ + güç (Recovery) veya fastboot reboot recovery."
            };
        }

        Report(progress, CustomRomInstallStage.FinalizingBoot, 99, "Telefon yeniden başlatılıyor…");
        var reboot = await FastbootDeviceProbe.RebootAsync(serial, "", cancellationToken).ConfigureAwait(false);
        if (!reboot.Success)
        {
            logger.Warning("[CustomROM] fastboot reboot failed: {Msg}", reboot.Message);
            return new DeviceToolResult
            {
                Success = true,
                Message =
                    "Kurulum tamam; otomatik yeniden başlatma başarısız.\n\n" +
                    "Telefonda Reboot → System seçin veya güç tuşuna basın."
            };
        }

        return new DeviceToolResult
        {
            Success = true,
            Message = "Boot imajları yazıldı — telefon yeniden başlatılıyor."
        };
    }

    private static async Task<DeviceToolResult> FlashPartitionWithMirrorAsync(
        string serial,
        string partition,
        string imagePath,
        string activeSlot,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var candidates = XiaomiAbFlashProfile.GetFlashTargetCandidates(partition, activeSlot);
        DeviceToolResult? last = null;
        string? successfulTarget = null;

        foreach (var target in candidates)
        {
            last = await FastbootDeviceProbe
                .FlashPartitionAsync(serial, target, imagePath, cancellationToken)
                .ConfigureAwait(false);
            if (last.Success)
            {
                successfulTarget = target;
                logger.Information("[CustomROM] Flashed {Partition} → {Target}", partition, target);
                break;
            }
        }

        if (successfulTarget is null)
        {
            return new DeviceToolResult
            {
                Success = false,
                Message =
                    $"{partition} yazılamadı:\n{last?.Message ?? "bilinmeyen hata"}\n\n" +
                    "Fastboot bağlantısını ve bootloader kilidini kontrol edin."
            };
        }

        var mirror = XiaomiAbFlashProfile.GetMirrorSlotTarget(successfulTarget);
        if (mirror is not null && !mirror.Equals(successfulTarget, StringComparison.OrdinalIgnoreCase))
        {
            var mirrorResult = await FastbootDeviceProbe
                .FlashPartitionAsync(serial, mirror, imagePath, cancellationToken)
                .ConfigureAwait(false);
            if (!mirrorResult.Success)
            {
                logger.Warning(
                    "[CustomROM] Mirror flash {Mirror} failed (active slot OK): {Msg}",
                    mirror,
                    mirrorResult.Message);
            }
        }

        return new DeviceToolResult { Success = true, Message = $"{partition} yazıldı." };
    }

    private static async Task<string> ResolveActiveSlotAsync(
        IFastbootDiscoveryService fastboot,
        string serial,
        CancellationToken cancellationToken)
    {
        var raw = await fastboot.GetVariableAsync(serial, "current-slot", cancellationToken).ConfigureAwait(false);
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

    private static async Task<string?> WaitForFastbootSerialAsync(
        IFastbootDiscoveryService fastboot,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var serial = await fastboot.GetFirstSerialAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(serial))
                return serial;

            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static IProgress<FlashingProgressReport>? MapFlashProgress(
        IProgress<CustomRomInstallProgress>? progress,
        int minPercent,
        int maxPercent) =>
        progress is null
            ? null
            : new Progress<FlashingProgressReport>(p =>
            {
                var mapped = minPercent + (int)Math.Round((p.Percent / 100.0) * (maxPercent - minPercent));
                progress.Report(new CustomRomInstallProgress
                {
                    Stage = CustomRomInstallStage.PatchingFstab,
                    Percent = Math.Clamp(mapped, minPercent, maxPercent),
                    Message = p.Message
                });
            });

    private static IProgress<FlashingProgressReport>? MapProgress(
        IProgress<CustomRomInstallProgress>? progress,
        int minPercent,
        int maxPercent) =>
        progress is null
            ? null
            : new Progress<FlashingProgressReport>(p =>
            {
                var mapped = minPercent + (int)Math.Round((p.Percent / 100.0) * (maxPercent - minPercent));
                progress.Report(new CustomRomInstallProgress
                {
                    Stage = CustomRomInstallStage.FinalizingBoot,
                    Percent = Math.Clamp(mapped, minPercent, maxPercent),
                    Message = p.Message
                });
            });

    private static void Report(
        IProgress<CustomRomInstallProgress>? progress,
        CustomRomInstallStage stage,
        int percent,
        string message) =>
        progress?.Report(new CustomRomInstallProgress
        {
            Stage = stage,
            Percent = Math.Clamp(percent, 0, 100),
            Message = message
        });

    private static DeviceToolResult Fail(string message) => new() { Success = false, Message = message };
}
