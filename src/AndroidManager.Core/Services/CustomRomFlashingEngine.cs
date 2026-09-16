using System.Globalization;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;

namespace AndroidManager.Core.Services;

/// <summary>
/// Payload ROM zip → extract → payload dump → fastboot/fastbootd flash pipeline.
/// Xiaomi vili: flash_all.bat sırası (_ab partition adları, vbmeta en sonda, AVB bayrağı yok).
/// </summary>
public sealed class CustomRomFlashingEngine : ICustomRomFlashingEngine
{
    private static readonly string[] GenericFlashOrder =
    [
        "boot", "vendor_boot", "dtbo", "vbmeta", "super", "cust", "userdata"
    ];

    private static readonly string[] GenericBootloaderPreflash =
    [
        "boot", "vendor_boot", "dtbo", "init_boot"
    ];

    private readonly PayloadBinDumper _dumper;
    private readonly FastbootFlashRunner _fastboot;

    public CustomRomFlashingEngine()
        : this(new PayloadBinDumper(), new FastbootFlashRunner())
    {
    }

    public CustomRomFlashingEngine(PayloadBinDumper dumper, FastbootFlashRunner fastboot)
    {
        _dumper = dumper;
        _fastboot = fastboot;
    }

    public async Task<IReadOnlyList<string>> PreviewPartitionsAsync(
        string zipPath,
        CancellationToken cancellationToken = default)
    {
        var workDir = CreateWorkDirectory(null);
        try
        {
            var extracted = await RomZipExtractor.ExtractAsync(zipPath, workDir, progress: null, cancellationToken)
                .ConfigureAwait(false);
            if (!extracted.HasPayload || extracted.PayloadBinPath is null)
                return [];

            var imagesDir = Path.Combine(workDir, "images");
            var images = await _dumper.DumpAsync(extracted.PayloadBinPath, imagesDir, progress: null, cancellationToken)
                .ConfigureAwait(false);
            return images.Select(i => i.PartitionName).ToArray();
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    public async Task<CustomRomFlashResult> FlashAsync(
        CustomRomFlashRequest request,
        IProgress<FlashingProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ZipPath) || !File.Exists(request.ZipPath))
            return Fail("ROM zip dosyası bulunamadı.");

        var workDir = RomExtractionCache.ResolveCacheDirectory(request.ZipPath, request.WorkingDirectory);
        Directory.CreateDirectory(workDir);
        var imagesDir = Path.Combine(workDir, "images");
        var flashed = new List<string>();
        var reuseCache = request.ReuseCachedExtraction;

        try
        {
            Report(progress, CustomRomFlashStage.Extracting, 1, "Pipeline başlıyor…");

            IReadOnlyDictionary<string, string> imageMap;

            if (reuseCache && RomExtractionCache.TryLoadImages(workDir, request.ZipPath, out var cached))
            {
                Report(progress, CustomRomFlashStage.DumpingPayload, 44,
                    $"Önbellekten {cached.Count} imaj yüklendi — zip/payload çıkarma atlandı.");
                imageMap = cached;
            }
            else
            {
                var extracted = await RomZipExtractor
                    .ExtractAsync(request.ZipPath, workDir, progress, cancellationToken, reuseExisting: reuseCache)
                    .ConfigureAwait(false);

                if (!extracted.HasPayload || extracted.PayloadBinPath is null)
                {
                    return Fail(
                        "Bu zip payload.bin içermiyor. Recovery zip ROM'lar için TWRP sideload yolunu kullanın.");
                }

                Report(progress, CustomRomFlashStage.DumpingPayload, 10, "payload.bin parçalanıyor…");
                var images = await _dumper
                    .DumpAsync(extracted.PayloadBinPath, imagesDir, progress, cancellationToken)
                    .ConfigureAwait(false);

                if (images.Count == 0)
                    return Fail("payload.bin'den partition imajı çıkarılamadı.");

                RomExtractionCache.SaveManifest(workDir, request.ZipPath, images.Count);

                Report(progress, CustomRomFlashStage.DumpingPayload, 44,
                    $"İmajlar önbelleğe alındı — sonraki flash'ta çıkarma atlanır.");

                imageMap = images.ToDictionary(
                    i => i.PartitionName,
                    i => i.ImagePath,
                    StringComparer.OrdinalIgnoreCase);
            }

            Report(progress, CustomRomFlashStage.PreparingFastboot, 45, "Fastboot cihazı aranıyor…");
            var serial = await ResolveSerialAsync(request, cancellationToken).ConfigureAwait(false);
            if (serial is null)
            {
                return Fail(
                    "Fastboot cihazı bulunamadı. Bootloader/fastboot ekranında USB bağlı olsun veya EnsureBootloaderAsync sağlayın.");
            }

            if (!await WaitForFastbootAsync(serial, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false))
            {
                if (request.EnsureBootloaderAsync is not null)
                {
                    Report(progress, CustomRomFlashStage.PreparingFastboot, 46, "Bootloader moduna alınıyor…");
                    await request.EnsureBootloaderAsync(cancellationToken).ConfigureAwait(false);
                    await Task.Delay(6000, cancellationToken).ConfigureAwait(false);
                    serial = await ResolveSerialAsync(request, cancellationToken).ConfigureAwait(false);
                }
            }

            if (serial is null || !await WaitForFastbootAsync(serial, TimeSpan.FromSeconds(25), cancellationToken).ConfigureAwait(false))
                return Fail("Fastboot bağlantısı kurulamadı.");

            var productVar = await _fastboot.GetVarAsync(serial, "product", cancellationToken).ConfigureAwait(false);
            var useViliProfile = XiaomiAbFlashProfile.IsViliDevice(request.DeviceCodename, ParseGetVar(productVar));

            if (useViliProfile)
            {
                Report(progress, CustomRomFlashStage.PreparingFastboot, 46,
                    "Xiaomi vili: vbmeta → boot → fastbootd → system/vendor…");
                return await FlashViliProfileAsync(
                    request, serial, imageMap, flashed, workDir, imagesDir, progress, cancellationToken)
                    .ConfigureAwait(false);
            }

            return await FlashGenericProfileAsync(
                    request, serial, imageMap, flashed, workDir, imagesDir, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Report(progress, CustomRomFlashStage.Cancelled, 0, "İşlem iptal edildi.");
            return Fail("İşlem iptal edildi.");
        }
        catch (Exception ex)
        {
            Report(progress, CustomRomFlashStage.Failed, 0, ex.Message);
            return Fail(ex.Message);
        }
    }

    private async Task<CustomRomFlashResult> FlashViliProfileAsync(
        CustomRomFlashRequest request,
        string serial,
        IReadOnlyDictionary<string, string> imageMap,
        List<string> flashed,
        string workDir,
        string imagesDir,
        IProgress<FlashingProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var activeSlot = await ResolveActiveSlotAsync(serial, request.ActiveSlot, cancellationToken)
            .ConfigureAwait(false);

        var skippedFirmware = XiaomiAbFlashProfile.ListSkippedFirmware(imageMap);
        foreach (var partition in skippedFirmware)
            planned.Add(partition);

        if (skippedFirmware.Count > 0)
        {
            var sample = string.Join(", ", skippedFirmware.Take(8));
            var more = skippedFirmware.Count > 8 ? $" +{skippedFirmware.Count - 8}" : "";
            Report(progress, CustomRomFlashStage.PreparingFastboot, 47,
                $"Firmware atlanıyor (stock bat kapsamı): {sample}{more}");
        }

        // Bootloader partition'ları fastbootd sonrasına (stock bat sırası)
        foreach (var deferred in new[] { "vendor_boot", "dtbo", "vbmeta", "vbmeta_system", "boot", "init_boot" })
        {
            if (imageMap.ContainsKey(deferred))
                planned.Add(deferred);
        }

        var unlockError = await VerifyBootloaderUnlockedForFastbootdAsync(serial, cancellationToken).ConfigureAwait(false);
        if (unlockError is not null)
        {
            return new CustomRomFlashResult
            {
                Success = false,
                Message = unlockError,
                FlashedPartitions = flashed,
                ExtractedRoot = workDir,
                ImagesDirectory = imagesDir
            };
        }

        var inFastbootd = await _fastboot.IsFastbootdAsync(serial, cancellationToken).ConfigureAwait(false);
        string? superPathOverride = null;

        if (!inFastbootd)
        {
            if (!await EnsureBootloaderFastbootAsync(serial, progress, cancellationToken).ConfigureAwait(false))
            {
                return Fail("Bootloader fastboot moduna geçilemedi (vbmeta burada yazılmalı).");
            }

            var phaseVbmeta = XiaomiAbFlashProfile.BuildPlan(
                XiaomiAbFlashProfile.BeforeFastbootdVbmetaOrder, imageMap, activeSlot);
            if (phaseVbmeta.Count > 0)
            {
                Report(progress, CustomRomFlashStage.PreparingFastboot, 48,
                    "1/3 — Dijital imza kapatılıyor (vbmeta --disable-verity --disable-verification)…");
                var vb = await FlashVbmetaBootloaderPhaseAsync(
                        serial, phaseVbmeta, workDir, flashed, progress, 48, 51, activeSlot, cancellationToken)
                    .ConfigureAwait(false);
                if (!vb.Success)
                {
                    return new CustomRomFlashResult
                    {
                        Success = false,
                        Message = vb.Message,
                        FlashedPartitions = flashed,
                        ExtractedRoot = workDir,
                        ImagesDirectory = imagesDir
                    };
                }
            }

            var bootstrap = XiaomiAbFlashProfile.BuildPlan(
                XiaomiAbFlashProfile.BeforeFastbootdBootstrapOrder, imageMap, activeSlot);
            if (bootstrap.Count > 0)
            {
                Report(progress, CustomRomFlashStage.PreparingFastboot, 51,
                    "2/3 — Normal fastboot: vendor_boot + boot…");
                var prep = await FlashPlanAsync(
                        serial, bootstrap, flashed, progress, 51, 54,
                        useXiaomiAvb: false, useXiaomiFallbacks: true, activeSlot, cancellationToken)
                    .ConfigureAwait(false);
                if (!prep.Success)
                {
                    return new CustomRomFlashResult
                    {
                        Success = false,
                        Message = prep.Message,
                        FlashedPartitions = flashed,
                        ExtractedRoot = workDir,
                        ImagesDirectory = imagesDir
                    };
                }

                foreach (var (partition, _, _) in bootstrap)
                    planned.Add(partition);
            }

            var superBuild = await EnsureSuperImageForFastbootdAsync(
                    serial, imageMap, workDir, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!superBuild.Success)
            {
                return new CustomRomFlashResult
                {
                    Success = false,
                    Message = superBuild.ErrorMessage ?? "super.img oluşturulamadı.",
                    FlashedPartitions = flashed,
                    ExtractedRoot = workDir,
                    ImagesDirectory = imagesDir
                };
            }

            superPathOverride = superBuild.Path;

            Report(progress, CustomRomFlashStage.EnteringFastbootd, 54,
                "3/3 — fastbootd'ye geçiliyor (is-userspace: yes doğrulanacak)…");
            var fastbootdEntry = await EnterFastbootdAsync(
                    serial, imageMap, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!fastbootdEntry.Success)
            {
                return new CustomRomFlashResult
                {
                    Success = false,
                    Message = fastbootdEntry.Message ??
                              "fastbootd moduna geçilemedi.",
                    FlashedPartitions = flashed,
                    ExtractedRoot = workDir,
                    ImagesDirectory = imagesDir
                };
            }

            serial = await ResolveSerialAsync(request, cancellationToken).ConfigureAwait(false) ?? serial;
        }
        else
        {
            Report(progress, CustomRomFlashStage.PreparingFastboot, 48,
                "Zaten fastbootd'de — vbmeta atlanır; super.img hazırlanıyor…");

            var superBuild = await EnsureSuperImageForFastbootdAsync(
                    serial, imageMap, workDir, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!superBuild.Success)
            {
                return new CustomRomFlashResult
                {
                    Success = false,
                    Message = superBuild.ErrorMessage ?? "super.img oluşturulamadı.",
                    FlashedPartitions = flashed,
                    ExtractedRoot = workDir,
                    ImagesDirectory = imagesDir
                };
            }

            superPathOverride = superBuild.Path;
        }

        var phase2 = BuildViliFastbootdPlan(imageMap, superPathOverride);

        var fastbootdPlan = phase2
            .Where(p => !IsViliDeferredBootloaderPartition(p.Partition))
            .ToList();

        foreach (var (partition, _, _) in fastbootdPlan)
            planned.Add(partition);

        if (fastbootdPlan.Count == 0)
        {
            return Fail(
                "Fastbootd'de flash edilecek super.img bulunamadı.\n\n" +
                DynamicSuperImageBuilder.BuildLpmakeMissingMessage());
        }

        if (!fastbootdPlan.Any(p => p.Partition.Equals("super", StringComparison.OrdinalIgnoreCase)))
        {
            return Fail(
                "Xiaomi fastbootd: system/vendor ayrı flash desteklenmiyor (resize / locked devices).\n\n" +
                DynamicSuperImageBuilder.BuildLpmakeMissingMessage());
        }

        Report(progress, CustomRomFlashStage.Flashing, 55,
            "Fastbootd: super yazılıyor (system + vendor + product içinde)…");

        var dyn = await FlashPlanAsync(
                serial, fastbootdPlan, flashed, progress, 55, 82,
                useXiaomiAvb: false, useXiaomiFallbacks: false, activeSlot, cancellationToken)
            .ConfigureAwait(false);
        if (!dyn.Success)
        {
            return new CustomRomFlashResult
            {
                Success = false,
                Message = dyn.Message,
                FlashedPartitions = flashed,
                ExtractedRoot = workDir,
                ImagesDirectory = imagesDir
            };
        }

        Report(progress, CustomRomFlashStage.PreparingFastboot, 83, "Bootloader moduna dönülüyor (boot en sonda — bat)…");
        _ = await _fastboot.RebootBootloaderAsync(serial, progress, cancellationToken).ConfigureAwait(false);
        await Task.Delay(4000, cancellationToken).ConfigureAwait(false);
        if (!await WaitForFastbootAsync(serial, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false))
            return Fail("boot flash için bootloader fastboot'a dönülemedi.");

        var phase3 = XiaomiAbFlashProfile.BuildPlan(
            XiaomiAbFlashProfile.FinalBootloaderOrder, imageMap, activeSlot);
        phase3 = phase3
            .Where(p => !flashed.Contains(p.Partition, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (request.WipeUserData && imageMap.TryGetValue("userdata", out var userdataPath) && File.Exists(userdataPath))
        {
            phase3.Add(("userdata", "userdata", userdataPath));
        }

        if (phase3.Any(p => p.Partition.Equals("boot", StringComparison.OrdinalIgnoreCase)))
        {
            Report(progress, CustomRomFlashStage.Flashing, 84,
                $"erase {XiaomiAbFlashProfile.ResolveFlashTarget("boot", activeSlot)} (yeni boot öncesi)…");
            _ = await _fastboot
                .ErasePartitionAsync(
                    serial,
                    XiaomiAbFlashProfile.ResolveFlashTarget("boot", activeSlot),
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (phase3.Count > 0)
        {
            var fin = await FlashPlanAsync(
                    serial, phase3, flashed, progress, 84, 90,
                    useXiaomiAvb: false, useXiaomiFallbacks: true, activeSlot, cancellationToken)
                .ConfigureAwait(false);
            if (!fin.Success)
            {
                return new CustomRomFlashResult
                {
                    Success = false,
                    Message = fin.Message,
                    FlashedPartitions = flashed,
                    ExtractedRoot = workDir,
                    ImagesDirectory = imagesDir
                };
            }
        }

        return await FinalizeFlashAsync(request, serial, flashed, workDir, imagesDir, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CustomRomFlashResult> FlashGenericProfileAsync(
        CustomRomFlashRequest request,
        string serial,
        IReadOnlyDictionary<string, string> imageMap,
        List<string> flashed,
        string workDir,
        string imagesDir,
        IProgress<FlashingProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        var fullPlan = BuildGenericFlashPlan(imageMap, request.WipeUserData);
        if (fullPlan.Count == 0)
        {
            var found = string.Join(", ", imageMap.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
            return Fail($"Flash edilecek imaj bulunamadı. Çıkarılan partition'lar: {found}.");
        }

        var inFastbootd = await _fastboot.IsFastbootdAsync(serial, cancellationToken).ConfigureAwait(false);

        if (!inFastbootd)
        {
            var preflash = BuildGenericPreflashPlan(imageMap);
            if (preflash.Count > 0)
            {
                Report(progress, CustomRomFlashStage.PreparingFastboot, 47,
                    "Bootloader fastboot: boot imajları yazılıyor…");
                var preResult = await FlashPlanAsync(
                        serial,
                        preflash.Select(p => (p.Partition, p.Partition, p.Path)).ToList(),
                        flashed,
                        progress,
                        47,
                        52,
                        useXiaomiAvb: false,
                        useXiaomiFallbacks: false,
                        activeSlot: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!preResult.Success)
                {
                    return new CustomRomFlashResult
                    {
                        Success = false,
                        Message = preResult.Message,
                        FlashedPartitions = flashed,
                        ExtractedRoot = workDir,
                        ImagesDirectory = imagesDir
                    };
                }
            }

            Report(progress, CustomRomFlashStage.EnteringFastbootd, 52, "fastbootd moduna geçiliyor…");
            var fastbootdEntry = await EnterFastbootdAsync(
                    serial, imageMap, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!fastbootdEntry.Success)
            {
                return Fail(fastbootdEntry.Message ??
                            "fastbootd moduna geçilemedi. Bootloader fastboot'a alıp tekrar deneyin veya TWRP sideload kullanın.");
            }

            serial = await ResolveSerialAsync(request, cancellationToken).ConfigureAwait(false) ?? serial;
        }

        var preflashSet = new HashSet<string>(GenericBootloaderPreflash, StringComparer.OrdinalIgnoreCase);
        var fastbootdPlan = fullPlan
            .Where(p => !preflashSet.Contains(p.Partition))
            .Select(p => (p.Partition, p.Partition, p.Path))
            .ToList();

        if (fastbootdPlan.Count == 0)
            fastbootdPlan = fullPlan.Select(p => (p.Partition, p.Partition, p.Path)).ToList();

        var dyn = await FlashPlanAsync(
                serial, fastbootdPlan, flashed, progress, 55, 88,
                useXiaomiAvb: false, useXiaomiFallbacks: false, activeSlot: null, cancellationToken)
            .ConfigureAwait(false);
        if (!dyn.Success)
        {
            return new CustomRomFlashResult
            {
                Success = false,
                Message = dyn.Message,
                FlashedPartitions = flashed,
                ExtractedRoot = workDir,
                ImagesDirectory = imagesDir
            };
        }

        return await FinalizeFlashAsync(request, serial, flashed, workDir, imagesDir, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CustomRomFlashResult> FinalizeFlashAsync(
        CustomRomFlashRequest request,
        string serial,
        List<string> flashed,
        string workDir,
        string imagesDir,
        IProgress<FlashingProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, CustomRomFlashStage.Finalizing, 92, $"Aktif slot: {request.ActiveSlot}");
        var active = await _fastboot
            .SetActiveSlotAsync(serial, request.ActiveSlot, progress, cancellationToken)
            .ConfigureAwait(false);
        if (!active.Success)
            return Fail($"set_active {request.ActiveSlot} başarısız:\n{active.Message}");

        Report(progress, CustomRomFlashStage.Finalizing, 96, "Yeniden başlatılıyor…");
        _ = await _fastboot.RebootAsync(serial, progress, cancellationToken).ConfigureAwait(false);

        Report(progress, CustomRomFlashStage.Completed, 100, "Custom ROM fastboot flash tamamlandı.");
        return new CustomRomFlashResult
        {
            Success = true,
            Message = "Payload ROM fastboot/fastbootd ile yüklendi. İlk açılış birkaç dakika sürebilir.",
            FlashedPartitions = flashed,
            ExtractedRoot = workDir,
            ImagesDirectory = imagesDir
        };
    }

    private static bool IsViliDeferredBootloaderPartition(string partition) =>
        partition.Equals("boot", StringComparison.OrdinalIgnoreCase)
        || partition.Equals("init_boot", StringComparison.OrdinalIgnoreCase)
        || partition.Equals("vendor_boot", StringComparison.OrdinalIgnoreCase)
        || partition.Equals("dtbo", StringComparison.OrdinalIgnoreCase)
        || partition.Equals("vbmeta", StringComparison.OrdinalIgnoreCase)
        || partition.Equals("vbmeta_system", StringComparison.OrdinalIgnoreCase)
        || partition.Equals("userdata", StringComparison.OrdinalIgnoreCase);

    private async Task<string> ResolveActiveSlotAsync(
        string serial,
        string fallbackSlot,
        CancellationToken cancellationToken)
    {
        var raw = await _fastboot.GetVarAsync(serial, "current-slot", cancellationToken).ConfigureAwait(false);
        var slot = ParseGetVar(raw).Trim().ToLowerInvariant();
        return slot is "a" or "b" ? slot : fallbackSlot.Trim().ToLowerInvariant();
    }

    private async Task<string?> VerifyBootloaderUnlockedForFastbootdAsync(
        string serial,
        CancellationToken cancellationToken)
    {
        var unlockedRaw = await _fastboot.GetVarAsync(serial, "unlocked", cancellationToken).ConfigureAwait(false);
        var unlocked = ParseGetVar(unlockedRaw);
        if (unlocked.Equals("yes", StringComparison.OrdinalIgnoreCase))
            return null;

        return
            "Bootloader kilitli görünüyor (fastboot getvar unlocked ≠ yes).\n\n" +
            "Payload ROM'da system/vendor fastbootd'de yazılır; bu işlem kilitli cihazda çalışmaz.\n" +
            "Mi Unlock ile bootloader'ı açın veya stock ROM bat (super.img) kullanın.\n\n" +
            $"Şu an: unlocked={(string.IsNullOrWhiteSpace(unlocked) ? "(boş)" : unlocked)}";
    }

    private static List<(string Partition, string FlashTarget, string Path)> BuildViliFastbootdPlan(
        IReadOnlyDictionary<string, string> imageMap,
        string? superPathOverride) =>
        XiaomiAbFlashProfile.BuildFastbootdPlan(imageMap, superPathOverride);

    private async Task<DynamicSuperImageBuilder.SuperImageBuildResult> EnsureSuperImageForFastbootdAsync(
        string serial,
        IReadOnlyDictionary<string, string> imageMap,
        string workDir,
        IProgress<FlashingProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        if (imageMap.TryGetValue("super", out var romSuper)
            && File.Exists(romSuper)
            && new FileInfo(romSuper).Length > 0)
        {
            Report(progress, CustomRomFlashStage.PreparingFastboot, 53,
                "ROM super.img içeriyor — lpmake gerekmez.");
            return new DynamicSuperImageBuilder.SuperImageBuildResult(true, romSuper, null);
        }

        if (!DynamicSuperImageBuilder.HasSuperLogicalImages(imageMap))
        {
            return new DynamicSuperImageBuilder.SuperImageBuildResult(
                false,
                null,
                "Payload'da system/vendor/product imajı yok.");
        }

        var superSize = await GetSuperPartitionSizeBytesAsync(serial, cancellationToken).ConfigureAwait(false);
        var builtPath = Path.Combine(workDir, "built_super.img");
        Report(progress, CustomRomFlashStage.PreparingFastboot, 52,
            "lpmake: system+vendor+product → super.img (fastbootd öncesi)…");

        return await DynamicSuperImageBuilder
            .BuildSuperImageAsync(imageMap, builtPath, superSize, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<long> GetSuperPartitionSizeBytesAsync(
        string serial,
        CancellationToken cancellationToken)
    {
        const long defaultViliSuper = 0x220000000L;
        var raw = await _fastboot.GetVarAsync(serial, "partition-size:super", cancellationToken).ConfigureAwait(false);
        var value = ParseGetVar(raw).Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
        {
            return hex;
        }

        return long.TryParse(value, out var dec) ? dec : defaultViliSuper;
    }

    /// <summary>vbmeta yalnızca bootloader fastboot'ta yazılır; fastbootd'de locked hatası verir.</summary>
    private async Task<CustomRomFlashResult> FlashVbmetaBootloaderPhaseAsync(
        string serial,
        IReadOnlyList<(string Partition, string FlashTarget, string Path)> plan,
        string workDir,
        List<string> flashed,
        IProgress<FlashingProgressReport>? progress,
        int percentStart,
        int percentEnd,
        string? activeSlot,
        CancellationToken cancellationToken)
    {
        if (await _fastboot.IsFastbootdAsync(serial, cancellationToken).ConfigureAwait(false))
        {
            return new CustomRomFlashResult
            {
                Success = false,
                Message =
                    "vbmeta bootloader fastboot'ta yazılmalıdır; fastbootd/recovery modunda yazılamaz.\n" +
                    "Telefonu bootloader fastboot'a alıp tekrar deneyin."
            };
        }

        var step = 0;
        foreach (var (partition, flashTarget, romPath) in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            step++;
            var pct = percentStart + (int)Math.Round(step / (double)plan.Count * (percentEnd - percentStart));

            var stubPath = await VbmetaImageHelper
                .WriteDisabledStubAsync(workDir, partition, cancellationToken)
                .ConfigureAwait(false);

            var romHasAvb = AvbImageHelper.HasAvbMagic(romPath);
            if (!romHasAvb)
            {
                Report(progress, CustomRomFlashStage.Flashing, pct,
                    $"{partition}: ROM vbmeta AVB0 içermiyor — devre dışı stub kullanılıyor", partition);
            }

            var romDisabledCopy = romHasAvb
                ? await VbmetaImageHelper
                    .WriteDisabledRomCopyAsync(romPath, workDir, partition, cancellationToken)
                    .ConfigureAwait(false)
                : null;

            var targets = XiaomiAbFlashProfile.GetFlashTargetCandidates(partition, activeSlot);
            FastbootCommandResult? result = null;
            var triedTarget = flashTarget;
            var usedDisableFlags = false;
            var flashPath = romPath;

            foreach (var target in targets)
            {
                triedTarget = target;
                foreach (var attempt in VbmetaImageHelper.EnumerateFlashAttempts(
                             romDisabledCopy, romPath, stubPath, romHasAvb))
                {
                    flashPath = attempt.ImagePath;
                    usedDisableFlags = attempt.DisableFlags;
                    var flagLabel = usedDisableFlags
                        ? " (--disable-verity --disable-verification)"
                        : " (flags image içinde)";
                    Report(progress, CustomRomFlashStage.Flashing, pct,
                        $"Flash: {partition} → {target}{flagLabel}", partition);

                    result = attempt.GlobalFlags
                        ? await _fastboot.FlashPartitionAsync(
                                serial, target, flashPath,
                                disableAvbFlags: true, progress, cancellationToken, globalAvbFlags: true)
                            .ConfigureAwait(false)
                        : await _fastboot.FlashPartitionAsync(
                                serial, target, flashPath, usedDisableFlags, progress, cancellationToken)
                            .ConfigureAwait(false);

                    if (result.Success)
                        break;

                    if (attempt.DisableFlags && AvbImageHelper.IsAvbMagicError(result.Message))
                        continue;
                }

                if (result?.Success == true)
                    break;
            }

            if (result is null || !result.Success)
            {
                return new CustomRomFlashResult
                {
                    Success = false,
                    Message =
                        $"{partition} → {triedTarget} vbmeta flash başarısız:\n{result?.Message}\n\n" +
                        "Dijital imza (AVB) kapatılamadı. Bootloader açık olmalı; " +
                        "komut: fastboot flash vbmeta_ab vbmeta.img --disable-verity --disable-verification",
                    FlashedPartitions = flashed
                };
            }

            flashed.Add(partition);

            var mirror = XiaomiAbFlashProfile.GetMirrorSlotTarget(triedTarget);
            if (mirror is not null && !mirror.Equals(triedTarget, StringComparison.OrdinalIgnoreCase))
            {
                Report(progress, CustomRomFlashStage.Flashing, pct,
                    $"Diğer slota kopyalanıyor: {mirror}", partition);
                var mirrorResult = await _fastboot
                    .FlashPartitionAsync(serial, mirror, flashPath, usedDisableFlags, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (!mirrorResult.Success && usedDisableFlags)
                {
                    mirrorResult = await _fastboot
                        .FlashPartitionAsync(
                            serial, mirror, flashPath,
                            disableAvbFlags: true, progress, cancellationToken, globalAvbFlags: true)
                        .ConfigureAwait(false);
                }

                if (!mirrorResult.Success)
                {
                    Report(progress, CustomRomFlashStage.Flashing, pct,
                        $"{mirror} atlandı (aktif slot yazıldı)", partition);
                }
            }
        }

        return new CustomRomFlashResult { Success = true };
    }

    private async Task<CustomRomFlashResult> FlashPlanAsync(
        string serial,
        IReadOnlyList<(string Partition, string FlashTarget, string Path)> plan,
        List<string> flashed,
        IProgress<FlashingProgressReport>? progress,
        int percentStart,
        int percentEnd,
        bool useXiaomiAvb,
        bool useXiaomiFallbacks,
        string? activeSlot,
        CancellationToken cancellationToken)
    {
        var step = 0;
        foreach (var (partition, flashTarget, path) in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            step++;
            var pct = percentStart + (int)Math.Round(step / (double)plan.Count * (percentEnd - percentStart));

            var wantsAvbDisable = useXiaomiAvb
                ? XiaomiAbFlashProfile.UseAvbDisableFlags(partition)
                : NeedsGenericAvbDisable(partition);
            var disableAvb = wantsAvbDisable && AvbImageHelper.HasAvbMagic(path);

            var targets = useXiaomiFallbacks
                ? XiaomiAbFlashProfile.GetFlashTargetCandidates(partition, activeSlot)
                : [flashTarget];

            FastbootCommandResult? result = null;
            var triedTarget = flashTarget;

            foreach (var target in targets)
            {
                triedTarget = target;
                var label = target.Equals(partition, StringComparison.OrdinalIgnoreCase)
                    ? partition
                    : $"{partition} → {target}";
                Report(progress, CustomRomFlashStage.Flashing, pct, $"Flash: {label}", partition);

                result = await _fastboot
                    .FlashPartitionAsync(serial, target, path, disableAvb, progress, cancellationToken)
                    .ConfigureAwait(false);

                if (result.Success)
                    break;

                if (!IsRetriablePartitionError(result.Message))
                    break;

                if (targets.Count > 1)
                {
                    Report(progress, CustomRomFlashStage.Flashing, pct,
                        $"{target} olmadı — alternatif partition deneniyor…", partition);
                }
            }

            if (result is null)
                continue;

            if (!result.Success
                && result.Message.Contains("zaman aşımı", StringComparison.OrdinalIgnoreCase))
            {
                Report(progress, CustomRomFlashStage.Flashing, pct,
                    $"{partition} zaman aşımı — yeniden deneniyor…", partition);
                await Task.Delay(4000, cancellationToken).ConfigureAwait(false);
                var devices = await _fastboot.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
                if (devices.Count > 0)
                {
                    serial = devices.FirstOrDefault(d => d.Equals(serial, StringComparison.OrdinalIgnoreCase))
                               ?? devices[0];
                }

                result = await _fastboot
                    .FlashPartitionAsync(serial, triedTarget, path, disableAvb, progress, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!result.Success
                && disableAvb
                && AvbImageHelper.IsAvbMagicError(result.Message))
            {
                Report(progress, CustomRomFlashStage.Flashing, pct,
                    $"{partition}: AVB bayrağı olmadan yeniden deneniyor…", partition);
                disableAvb = false;
                result = await _fastboot
                    .FlashPartitionAsync(serial, triedTarget, path, disableAvbFlags: false, progress, cancellationToken)
                    .ConfigureAwait(false);
            }

            // AVB_MAGIC sonrası global --disable-verity tekrar denemek aynı hatayı üretir.
            if (!result.Success
                && wantsAvbDisable
                && IsVbmetaPartition(partition)
                && !AvbImageHelper.IsAvbMagicError(result.Message)
                && AvbImageHelper.HasAvbMagic(path)
                && !await _fastboot.IsFastbootdAsync(serial, cancellationToken).ConfigureAwait(false))
            {
                Report(progress, CustomRomFlashStage.Flashing, pct,
                    $"{partition}: global AVB bayrakları deneniyor…", partition);
                result = await _fastboot
                    .FlashPartitionAsync(
                        serial, triedTarget, path,
                        disableAvbFlags: true, progress, cancellationToken, globalAvbFlags: true)
                    .ConfigureAwait(false);
                if (result.Success)
                    disableAvb = true;
            }

            if (!result.Success && IsVbmetaPartition(partition)
                && !await _fastboot.IsFastbootdAsync(serial, cancellationToken).ConfigureAwait(false))
            {
                Report(progress, CustomRomFlashStage.Flashing, pct,
                    $"{partition}: slot silinip ham yazılıyor…", partition);
                await EraseVbmetaSlotsAsync(serial, partition, progress, cancellationToken)
                    .ConfigureAwait(false);
                disableAvb = false;
                result = await _fastboot
                    .FlashPartitionAsync(serial, triedTarget, path, disableAvbFlags: false, progress, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!result.Success)
            {
                var message = EnrichFlashErrorMessage(result.Message, partition);
                var label = triedTarget.Equals(partition, StringComparison.OrdinalIgnoreCase)
                    ? partition
                    : $"{partition} → {triedTarget}";

                return new CustomRomFlashResult
                {
                    Success = false,
                    Message = $"{label} flash başarısız:\n{message}",
                    FlashedPartitions = flashed
                };
            }

            flashed.Add(partition);

            var mirrorDisableAvb = disableAvb;
            if (useXiaomiFallbacks
                && XiaomiAbFlashProfile.ShouldMirrorToOtherSlot(partition)
                && result.Success)
            {
                var mirror = XiaomiAbFlashProfile.GetMirrorSlotTarget(triedTarget);
                if (mirror is not null
                    && !mirror.Equals(triedTarget, StringComparison.OrdinalIgnoreCase))
                {
                    Report(progress, CustomRomFlashStage.Flashing, pct,
                        $"Diğer slota kopyalanıyor: {mirror}", partition);
                    var mirrorResult = await _fastboot
                        .FlashPartitionAsync(serial, mirror, path, mirrorDisableAvb, progress, cancellationToken)
                        .ConfigureAwait(false);
                    if (!mirrorResult.Success)
                    {
                        Report(progress, CustomRomFlashStage.Flashing, pct,
                            $"{mirror} atlandı (aktif slot yazıldı)", partition);
                    }
                }
            }
        }

        return new CustomRomFlashResult { Success = true };
    }

    private static bool IsRetriablePartitionError(string message) =>
        message.Contains("max allowed", StringComparison.OrdinalIgnoreCase)
        || message.Contains("too large", StringComparison.OrdinalIgnoreCase)
        || message.Contains("Volume Full", StringComparison.OrdinalIgnoreCase)
        || message.Contains("partition size: 0", StringComparison.OrdinalIgnoreCase);

    private static string EnrichFlashErrorMessage(string message, string partition)
    {
        if (message.Contains("locked devices", StringComparison.OrdinalIgnoreCase))
        {
            var hint = XiaomiAbFlashProfile.IsSuperLogicalPartition(partition)
                ? " Xiaomi fastbootd'de system/vendor ayrı flash resize gerektirir — super.img flash'layın " +
                  "(lpmake veya stock super.img)."
                : " vbmeta yalnızca bootloader fastboot'ta yazılmalıdır.";

            return message + hint;
        }

        if (IsRetriablePartitionError(message))
        {
            return message +
                   "\n\nCihaz önceki kısmi flash'tan bozulmuş olabilir. " +
                   "Stock flash_all.bat ile tam flash yapıp ardından crDroid'i tekrar deneyin. " +
                   "Alternatif: TWRP sideload.";
        }

        return message;
    }

    private static List<(string Partition, string Path)> BuildGenericFlashPlan(
        IReadOnlyDictionary<string, string> imageMap,
        bool wipeUserData)
    {
        var plan = new List<(string, string)>();
        foreach (var partition in GenericFlashOrder)
        {
            if (partition.Equals("userdata", StringComparison.OrdinalIgnoreCase) && !wipeUserData)
                continue;
            if (imageMap.TryGetValue(partition, out var path) && File.Exists(path))
                plan.Add((partition, path));
        }

        foreach (var extra in imageMap.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            if (GenericFlashOrder.Any(p => p.Equals(extra, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (extra.Equals("userdata", StringComparison.OrdinalIgnoreCase) && !wipeUserData)
                continue;
            plan.Add((extra, imageMap[extra]));
        }

        return plan;
    }

    private static List<(string Partition, string Path)> BuildGenericPreflashPlan(
        IReadOnlyDictionary<string, string> imageMap)
    {
        var plan = new List<(string, string)>();
        foreach (var partition in GenericBootloaderPreflash)
        {
            if (imageMap.TryGetValue(partition, out var path) && File.Exists(path))
                plan.Add((partition, path));
        }

        return plan;
    }

    private async Task<bool> EnsureBootloaderFastbootAsync(
        string serial,
        IProgress<FlashingProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        if (!await _fastboot.IsFastbootdAsync(serial, cancellationToken).ConfigureAwait(false))
            return true;

        Report(progress, CustomRomFlashStage.PreparingFastboot, 47,
            "Bootloader fastboot'a dönülüyor (vbmeta burada yazılır)…");
        _ = await _fastboot.RebootBootloaderAsync(serial, progress, cancellationToken).ConfigureAwait(false);
        await Task.Delay(4000, cancellationToken).ConfigureAwait(false);
        return await WaitForFastbootAsync(serial, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
    }

    private async Task<FastbootdEntryResult> EnterFastbootdAsync(
        string serial,
        IReadOnlyDictionary<string, string> imageMap,
        IProgress<FlashingProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        imageMap.TryGetValue("recovery", out var recoveryPath);
        return await FastbootdEntryHelper
            .TryEnterAsync(_fastboot, serial, recoveryPath, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsVbmetaPartition(string partition)
    {
        var stem = XiaomiAbFlashProfile.StripSlotSuffix(partition);
        return stem is "vbmeta" or "vbmeta_system";
    }

    private async Task EraseVbmetaSlotsAsync(
        string serial,
        string partition,
        IProgress<FlashingProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var slot in new[] { "a", "b" })
        {
            var target = XiaomiAbFlashProfile.ResolveFlashTarget(partition, slot);
            _ = await _fastboot
                .ErasePartitionAsync(serial, target, progress, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool NeedsGenericAvbDisable(string partition) =>
        partition.Equals("vbmeta", StringComparison.OrdinalIgnoreCase)
        || partition.Equals("vbmeta_system", StringComparison.OrdinalIgnoreCase);

    private static string ParseGetVar(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return "";

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

    private async Task<string?> ResolveSerialAsync(CustomRomFlashRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.DeviceSerial))
        {
            var devices = await _fastboot.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
            var match = devices.FirstOrDefault(d =>
                d.Equals(request.DeviceSerial, StringComparison.OrdinalIgnoreCase));
            return match ?? request.DeviceSerial;
        }

        var list = await _fastboot.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
        return list.FirstOrDefault();
    }

    private async Task<bool> WaitForFastbootAsync(string serial, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var devices = await _fastboot.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
            if (devices.Any(d => d.Equals(serial, StringComparison.OrdinalIgnoreCase)))
                return true;
            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static string CreateWorkDirectory(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            Directory.CreateDirectory(requested);
            return requested;
        }

        var root = Path.Combine(Path.GetTempPath(), "AndroidManager", "extracted_rom", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // temp cleanup best-effort
        }
    }

    private static void Report(
        IProgress<FlashingProgressReport>? progress,
        CustomRomFlashStage stage,
        int percent,
        string message,
        string? partition = null)
    {
        progress?.Report(new FlashingProgressReport
        {
            Stage = stage,
            Percent = Math.Clamp(percent, 0, 100),
            Message = message,
            Partition = partition
        });
    }

    private static CustomRomFlashResult Fail(string message) =>
        new() { Success = false, Message = message };
}
