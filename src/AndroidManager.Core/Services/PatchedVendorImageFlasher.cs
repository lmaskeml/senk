using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;

namespace AndroidManager.Core.Services;

/// <summary>
/// Android 15 EROFS vendor — DFE fstab içeren vendor.img fastbootd ile yazar.
/// Xiaomi: 4 KB vbmeta stub userspace fastbootd'yi bozar.
/// Sıra: fastbootd → vendor (pad) → (locked ise) ROM vbmeta+disable → tekrar;
/// hâlâ olmazsa DFE soft-skip (ROM sideload zaten tamam).
/// </summary>
public sealed class PatchedVendorImageFlasher
{
    private static readonly string[] VbmetaPartitionNames = ["vbmeta", "vbmeta_system"];

    private readonly FastbootFlashRunner _fastboot = new();
    private readonly PayloadBinDumper _dumper = new();

    public async Task<DeviceToolResult> FlashInFastbootdAsync(
        string serial,
        string vendorImagePath,
        string? romZipPath = null,
        IProgress<FlashingProgressReport>? progress = null,
        CancellationToken cancellationToken = default,
        string? twrpImagePath = null,
        IAdbService? adb = null)
    {
        if (string.IsNullOrWhiteSpace(serial))
            return Fail("Fastboot seri numarası yok.");

        if (string.IsNullOrWhiteSpace(vendorImagePath) || !File.Exists(vendorImagePath))
            return Fail($"vendor imajı bulunamadı: {vendorImagePath}");

        var workDir = !string.IsNullOrWhiteSpace(romZipPath)
            ? RomExtractionCache.ResolveCacheDirectory(romZipPath, null)
            : Path.Combine(Path.GetTempPath(), "AndroidManager", "vendor_flash");
        var imagesDir = Path.Combine(workDir, "images");
        Directory.CreateDirectory(imagesDir);

        if (!string.IsNullOrWhiteSpace(romZipPath))
        {
            Report(progress, 89, "ROM vbmeta imajları hazırlanıyor (stub yerine)…");
            await EnsureVbmetaImagesAsync(romZipPath, workDir, imagesDir, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        var originalVendor = Path.Combine(imagesDir, "vendor.img");

        // 1) Önce fastbootd — ROM'un kendi vbmeta'sı ile userspace açılır.
        Report(progress, 90, "Fastbootd moduna geçiliyor (vendor yazımı)…");
        var entry = await FastbootdEntryHelper
            .TryEnterAsync(_fastboot, serial, recoveryImagePath: null, progress, cancellationToken)
            .ConfigureAwait(false);

        if (!entry.Success)
        {
            Report(progress, 90, "Fastbootd açılamadı — boot imajları yenileniyor…");
            if (!await EnsureBootloaderAsync(serial, progress, cancellationToken).ConfigureAwait(false))
                return SoftSkipDfe(serial, progress, BuildFastbootdFailure(entry.Message));

            await FlashUserspaceBootImagesAsync(serial, imagesDir, progress, cancellationToken)
                .ConfigureAwait(false);

            entry = await FastbootdEntryHelper
                .TryEnterAsync(_fastboot, serial, recoveryImagePath: null, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!entry.Success)
            return SoftSkipDfe(serial, progress, BuildFastbootdFailure(entry.Message));

        if (!await _fastboot.IsFastbootdAsync(serial, cancellationToken).ConfigureAwait(false))
        {
            return SoftSkipDfe(serial, progress,
                "fastbootd doğrulanamadı (is-userspace ≠ yes).\n\n" +
                "Telefonda FASTBOOT / fastbootd ekranı olmalı.");
        }

        var activeSlot = await ResolveActiveSlotAsync(serial, cancellationToken).ConfigureAwait(false);
        var partitionSize = await TryGetVendorPartitionSizeAsync(serial, activeSlot, cancellationToken)
            .ConfigureAwait(false);
        var flashImagePath = VendorImagePadder.PrepareForFlash(
            vendorImagePath,
            File.Exists(originalVendor) ? originalVendor : null,
            Path.Combine(workDir, "patched_fstab"),
            partitionSize);

        Report(progress, 92,
            partitionSize is > 0
                ? $"Güvenlik ayarları yazılıyor (vendor, pad={partitionSize / (1024 * 1024)} MB)…"
                : "Güvenlik ayarları yazılıyor (vendor fastboot flash)…");

        var flash = await FlashVendorAsync(serial, flashImagePath, activeSlot, progress, cancellationToken)
            .ConfigureAwait(false);

        // 2) locked/resize → ROM vbmeta + disable (stub YOK tercihen)
        if (!flash.Success && IsAvbResizeBlock(flash.Message))
        {
            Report(progress, 88, "vendor resize engellendi — ROM vbmeta (--disable-verity) yazılıyor…");
            if (!await EnsureBootloaderAsync(serial, progress, cancellationToken).ConfigureAwait(false))
                return SoftSkipDfe(serial, progress, flash.Message + "\n\nBootloader'a dönülemedi.");

            var vbmeta = await BootloaderVbmetaFlasher
                .FlashDisableVerityAsync(_fastboot, serial, workDir, imagesDir, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!vbmeta.Success)
                return SoftSkipDfe(serial, progress, vbmeta.Message + "\n\n" + flash.Message);

            await FlashUserspaceBootImagesAsync(serial, imagesDir, progress, cancellationToken)
                .ConfigureAwait(false);

            Report(progress, 90, "Fastbootd tekrar deneniyor (ROM vbmeta sonrası)…");
            entry = await FastbootdEntryHelper
                .TryEnterAsync(_fastboot, serial, recoveryImagePath: null, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!entry.Success)
            {
                return SoftSkipDfe(serial, progress,
                    "vbmeta yazıldı ancak fastbootd açılamadı (userspace unbootable).\n\n" +
                    "ROM sideload tamam — DFE atlandı. İsteğe bağlı: TWRP DFE zip.\n\n" +
                    (entry.Message ?? "") + "\n\nİlk vendor hatası:\n" + flash.Message);
            }

            // Partition size yeniden ölç (slot değişmiş olabilir)
            activeSlot = await ResolveActiveSlotAsync(serial, cancellationToken).ConfigureAwait(false);
            partitionSize = await TryGetVendorPartitionSizeAsync(serial, activeSlot, cancellationToken)
                .ConfigureAwait(false);
            flashImagePath = VendorImagePadder.PrepareForFlash(
                vendorImagePath,
                File.Exists(originalVendor) ? originalVendor : null,
                Path.Combine(workDir, "patched_fstab"),
                partitionSize);

            Report(progress, 92, "vendor tekrar yazılıyor (fastbootd)…");
            flash = await FlashVendorAsync(serial, flashImagePath, activeSlot, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!flash.Success)
        {
            // 3) fastbootd locked → geçici TWRP + dd
            if (!string.IsNullOrWhiteSpace(twrpImagePath) && File.Exists(twrpImagePath))
            {
                if (adb is null)
                {
                    return SoftSkipDfe(serial, progress,
                        $"vendor yazılamadı:\n{flash.Message}\n\n" +
                        "TWRP dd için ADB servisi yok — ROM sideload tamam.");
                }

                Report(progress, 93, "fastbootd locked — TWRP dd ile vendor deneniyor…");
                var dd = await TwrpVendorDdWriter
                    .TryWriteAsync(
                        _fastboot,
                        adb,
                        serial,
                        flashImagePath,
                        twrpImagePath,
                        activeSlot,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (dd.Success)
                {
                    Report(progress, 94, "✅ vendor (DFE) TWRP dd ile yazıldı.");
                    return new DeviceToolResult { Success = true, Message = serial };
                }

                Report(progress, 93, "TWRP dd başarısız: " + dd.Message.Replace('\n', ' '));
            }

            return SoftSkipDfe(serial, progress,
                $"vendor yazılamadı:\n{flash.Message}\n\n" +
                "ROM sideload tamam. DFE atlandı — TWRP'de Disable Force Encrypt zip deneyin.");
        }

        Report(progress, 94, "✅ vendor (DFE fstab) yazıldı — bootloader'a dönülüyor…");
        _ = await _fastboot.RebootBootloaderAsync(serial, progress, cancellationToken).ConfigureAwait(false);
        await Task.Delay(6000, cancellationToken).ConfigureAwait(false);

        if (!await WaitForBootloaderAsync(serial, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false))
        {
            return new DeviceToolResult
            {
                Success = true,
                Message = serial
            };
        }

        return new DeviceToolResult { Success = true, Message = serial };
    }

    public const string DfeSkippedPrefix = "DFE_SKIPPED|";

    /// <summary>DFE başarısız olsa da sideload tamam — kurulumu düşürme.</summary>
    private static DeviceToolResult SoftSkipDfe(
        string serial,
        IProgress<FlashingProgressReport>? progress,
        string detail)
    {
        var shortDetail = detail.Replace("\r", " ").Replace("\n", " ").Trim();
        if (shortDetail.Length > 160)
            shortDetail = shortDetail[..160] + "…";

        Report(progress, 94,
            "⚠️ DFE vendor atlandı — ROM kuruldu. Şifreleme açık kalabilir. " + shortDetail);

        // Finalizer DFE_SKIPPED| prefix ile uyarı gösterir (yanlış ✅ yazmaz).
        return new DeviceToolResult { Success = true, Message = DfeSkippedPrefix + serial };
    }

    private async Task EnsureVbmetaImagesAsync(
        string romZipPath,
        string workDir,
        string imagesDir,
        IProgress<FlashingProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        var missing = VbmetaPartitionNames
            .Where(name =>
            {
                var path = Path.Combine(imagesDir, $"{name}.img");
                return !File.Exists(path) || new FileInfo(path).Length <= 256;
            })
            .ToArray();

        if (missing.Length == 0)
            return;

        var payloadPath = RomZipExtractor.FindPayloadBin(workDir);
        if (payloadPath is null)
        {
            payloadPath = await RomZipExtractor
                .ExtractPayloadBinOnlyAsync(romZipPath, Path.Combine(workDir, "payload_only"), cancellationToken)
                .ConfigureAwait(false);
        }

        if (payloadPath is null)
            return;

        Report(progress, 89, $"payload'dan çıkarılıyor: {string.Join(", ", missing)}…");
        _ = await _dumper
            .DumpPartitionsAsync(payloadPath, imagesDir, missing, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<long?> TryGetVendorPartitionSizeAsync(
        string serial,
        string activeSlot,
        CancellationToken cancellationToken)
    {
        foreach (var name in new[] { $"vendor_{activeSlot}", "vendor" })
        {
            var raw = await _fastboot
                .GetVarAsync(serial, $"partition-size:{name}", cancellationToken)
                .ConfigureAwait(false);
            var parsed = TryParseSizeValue(ParseGetVarValue(raw));
            if (parsed is > 0)
                return parsed;
        }

        // getvar all içinde ara
        var all = await _fastboot.GetVarAsync(serial, "all", cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(all))
        {
            foreach (var name in new[] { $"vendor_{activeSlot}", "vendor" })
            {
                foreach (var line in all.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!line.Contains($"partition-size:{name}", StringComparison.OrdinalIgnoreCase)
                        && !line.Contains($"{name}:", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var value = ParseGetVarValue(line);
                    var parsed = TryParseSizeValue(value);
                    if (parsed is > 0)
                        return parsed;
                }
            }
        }

        return null;
    }

    private static long? TryParseSizeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        value = value.Trim().TrimEnd(';', ',');
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var hex)
            && hex > 0)
            return hex;

        if (long.TryParse(value, out var dec) && dec > 0)
            return dec;

        // Xiaomi bazen 0x'siz hex döner
        if (value.Length >= 6
            && long.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var bareHex)
            && bareHex > 1_000_000)
            return bareHex;

        return null;
    }

    private async Task FlashUserspaceBootImagesAsync(
        string serial,
        string imagesDir,
        IProgress<FlashingProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(imagesDir))
            return;

        foreach (var name in new[] { "vendor_boot", "init_boot", "boot" })
        {
            var path = Path.Combine(imagesDir, $"{name}.img");
            if (!File.Exists(path) || new FileInfo(path).Length <= 0)
                continue;

            Report(progress, 91, $"fastbootd için {name} yazılıyor…");
            var slot = await ResolveActiveSlotAsync(serial, cancellationToken).ConfigureAwait(false);
            foreach (var target in XiaomiAbFlashProfile.GetFlashTargetCandidates(name, slot))
            {
                var result = await _fastboot
                    .FlashPartitionAsync(serial, target, path, progress: progress, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (result.Success)
                    break;
            }
        }
    }

    private async Task<bool> EnsureBootloaderAsync(
        string serial,
        IProgress<FlashingProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        if (await _fastboot.IsBootloaderFastbootAsync(serial, cancellationToken).ConfigureAwait(false))
            return true;

        if (await _fastboot.IsFastbootdAsync(serial, cancellationToken).ConfigureAwait(false))
        {
            _ = await _fastboot.RebootBootloaderAsync(serial, progress, cancellationToken).ConfigureAwait(false);
            await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
        }

        return await WaitForBootloaderAsync(serial, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
    }

    private async Task<DeviceToolResult> FlashVendorAsync(
        string serial,
        string imagePath,
        string activeSlot,
        IProgress<FlashingProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        var imageSize = new FileInfo(imagePath).Length;
        // Yalnızca aktif slot — diğer slot partition-size:0 → sparse/locked gürültüsü.
        var candidates = new List<string> { $"vendor_{activeSlot}", "vendor" };

        FastbootCommandResult? last = null;
        foreach (var target in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Boyut uyumsuzluğunda resize dene (vbmeta disable sonrası).
            Report(progress, 92, $"logical partition boyut ayarı: {target} → {imageSize}…");
            _ = await _fastboot
                .ResizeLogicalPartitionAsync(serial, target, imageSize, progress, cancellationToken)
                .ConfigureAwait(false);

            Report(progress, 93, $"fastboot flash {target}…");
            last = await _fastboot
                .FlashPartitionAsync(serial, target, imagePath, progress: progress, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (last.Success)
                return new DeviceToolResult { Success = true, Message = $"{target} yazıldı." };

            // Sparse yanlış algı / pad → ham yazım
            if (IsSparseOrResizeError(last.Message))
            {
                Report(progress, 93, $"fastboot flash:raw {target}…");
                last = await _fastboot
                    .FlashPartitionRawAsync(serial, target, imagePath, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (last.Success)
                    return new DeviceToolResult { Success = true, Message = $"{target} yazıldı (raw)." };
            }
        }

        return new DeviceToolResult
        {
            Success = false,
            Message = last?.Message ?? "vendor flash başarısız."
        };
    }

    private static bool IsSparseOrResizeError(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && (message.Contains("sparse", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Resizing", StringComparison.OrdinalIgnoreCase)
            || message.Contains("locked", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Download is not allowed", StringComparison.OrdinalIgnoreCase));

    private async Task<string> ResolveActiveSlotAsync(string serial, CancellationToken cancellationToken)
    {
        foreach (var key in new[] { "current-slot", "slot-successful", "slot-suffix" })
        {
            var raw = await _fastboot.GetVarAsync(serial, key, cancellationToken).ConfigureAwait(false);
            var value = ParseGetVarValue(raw).Trim().ToLowerInvariant();
            if (value is "a" or "b")
                return value;
            if (value is "_a" or "a)")
                return "a";
            if (value is "_b" or "b)")
                return "b";
            if (value.EndsWith("_a", StringComparison.Ordinal))
                return "a";
            if (value.EndsWith("_b", StringComparison.Ordinal))
                return "b";
        }

        // Sideload genelde inactive slot'a yazar; current-slot sideload sonrası yeni slot olmalı.
        return "a";
    }

    private static bool IsAvbResizeBlock(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("locked devices", StringComparison.OrdinalIgnoreCase)
               || message.Contains("not available on locked", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Download is not allowed on locked", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Resizing", StringComparison.OrdinalIgnoreCase)
               || message.Contains("resize", StringComparison.OrdinalIgnoreCase)
               || message.Contains("AVB", StringComparison.OrdinalIgnoreCase)
               || message.Contains("verity", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildFastbootdFailure(string? detail) =>
        "fastbootd açılamadı — vendor yazılamadı.\n\n" +
        (string.IsNullOrWhiteSpace(detail) ? "" : detail + "\n\n") +
        "Xiaomi: 4 KB vbmeta stub userspace fastboot'u bozabilir.\n" +
        "Manuel: TWRP DFE zip veya bootloader'da ROM vbmeta + --disable-verity.";

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

    private static async Task<bool> WaitForBootloaderAsync(
        string serial,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var runner = new FastbootFlashRunner();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await runner.IsBootloaderFastbootAsync(serial, cancellationToken).ConfigureAwait(false))
                return true;

            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static void Report(IProgress<FlashingProgressReport>? progress, int percent, string message) =>
        progress?.Report(new FlashingProgressReport
        {
            Stage = CustomRomFlashStage.Flashing,
            Percent = percent,
            Message = message
        });

    private static DeviceToolResult Fail(string message) => new() { Success = false, Message = message };
}
