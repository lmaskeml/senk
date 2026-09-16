using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Core.Services;
using Serilog;

namespace AndroidManager.Security.Services;

public sealed class CustomRomWizardService : ICustomRomWizardService
{
    private readonly IAdbService _adb;
    private readonly IRecoveryManagerService _recovery;
    private readonly ICustomRomFlashingEngine _flashingEngine;
    private readonly IDeviceToolsService _deviceTools;
    private readonly IFastbootDiscoveryService _fastboot;
    private readonly ILogger _logger;
    private readonly RomInstallFinalizer _finalizer = new();
    private readonly RomPcPrepareService _pcPrepare = new();

    public CustomRomWizardService(
        IAdbService adb,
        IRecoveryManagerService recovery,
        ICustomRomFlashingEngine flashingEngine,
        IDeviceToolsService deviceTools,
        IFastbootDiscoveryService fastboot,
        ILogger? logger = null)
    {
        _adb = adb;
        _recovery = recovery;
        _flashingEngine = flashingEngine;
        _deviceTools = deviceTools;
        _fastboot = fastboot;
        _logger = logger ?? Log.ForContext<CustomRomWizardService>();
    }

    public Task<CustomRomPackageInfo> InspectPackageAsync(string zipPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => CustomRomPackageInspector.Inspect(zipPath), cancellationToken);
    }

    public CustomRomCompatibilityResult EvaluateCompatibility(CustomRomPackageInfo package, string deviceCodename) =>
        CustomRomPackageInspector.Evaluate(package, deviceCodename);

    public async Task<DeviceToolResult> InstallAsync(
        string zipPath,
        CustomRomInstallOptions options,
        IProgress<CustomRomInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            return Fail("ROM zip dosyası bulunamadı.");

        var package = CustomRomPackageInspector.Inspect(zipPath);
        if (options.UseFastbootPayloadEngine)
        {
            if (!package.HasPayloadBin)
                return Fail("Fastboot motoru yalnızca payload.bin içeren A/B ROM paketleri için kullanılabilir.");

            return await InstallViaFastbootEngineAsync(zipPath, options, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        var session = await PrepareRecoverySessionAsync(zipPath, options, progress, cancellationToken).ConfigureAwait(false);
        if (session.Error is not null)
            return session.Error;

        Report(progress, CustomRomInstallStage.Validating, 10,
            session.FastbootFormatted
                ? "Format sonrası TWRP ADB bekleniyor (USB yeniden bağlanabilir)…"
                : "Kurulum modu bağlantısı bekleniyor…");

        var device = await WaitForRecoveryAdbReadyAsync(
                session.FastbootFormatted,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        if (device is null)
        {
            return Fail(session.FastbootFormatted
                ? "TWRP ADB gelmedi.\n\n" +
                  "Format Data USB yetkilendirmelerini sıfırlar (TWRP'de RSA gerekmez).\n" +
                  "• TWRP ana menüde kalın\n" +
                  "• USB kablosunu çıkar-tak veya farklı porta takın\n" +
                  "• Manager'da Yenile → kurulumu tekrar başlatın («Zaten TWRP'deyim»)"
                : "Cihaz bağlı değil. TWRP açıkken USB'yi takın, ardından Yenile.");
        }

        if (!device.IsAdbReady)
        {
            return Fail(
                $"ADB hazır değil (state: {device.State}).\n\n" +
                (session.FastbootFormatted
                    ? "Format Data sonrası birkaç saniye bekleyin veya USB'yi yeniden takın. TWRP recovery'de RSA onayı gerekmez."
                    : "TWRP'de USB debugging açık olsun; kablo/port değiştirip Yenile'ye basın."));
        }

        var status = await _recovery.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var inRecovery = status.ConnectionMode == DeviceConnectionMode.Recovery
                         || device.IsRecovery
                         || device.IsSideload;
        if (!inRecovery)
        {
            return Fail("Cihaz recovery'de görünmüyor. TWRP'de ADB açık olsun (Advanced → ADB Sideload kapalıyken bile ADB çalışır).");
        }

        if (status.Type is RecoveryType.Stock or RecoveryType.Unknown)
        {
            return Fail(
                "Stok veya bilinmeyen recovery algılandı. Custom ROM için TWRP/OrangeFox gerekir.\n\n" +
                "Hazırlık adımında cihazınıza uygun TWRP .img seçip «Kurulumdan önce TWRP yaz» işaretleyin.");
        }

        try
        {
            string sideloadZipPath = zipPath;
            var fstabPatchedOnPc = false;
            var skipDeviceFstabPatch = false;
            IReadOnlyList<PatchedFstabFile> fstabFilesToPush = [];
            string? patchedVendorImagePath = null;
            var dfeNeededButSkipped = false;

            if (package.HasPayloadBin && options.PatchFstabDisableForceEncrypt)
            {
                Report(progress, CustomRomInstallStage.Validating, 8, "ROM PC'de hazırlanıyor (fstab)…");
                var pcPrep = await _pcPrepare
                    .PrepareForSideloadAsync(
                        zipPath,
                        patchFstab: true,
                        MapPcPrepareProgress(progress),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!pcPrep.Success)
                {
                    Report(progress, CustomRomInstallStage.Failed, 0, pcPrep.Message);
                    return Fail(pcPrep.Message);
                }

                sideloadZipPath = pcPrep.SideloadZipPath;
                fstabPatchedOnPc = pcPrep.FstabPatchedOnPc;
                skipDeviceFstabPatch = pcPrep.SkipDeviceFstabPatch;
                fstabFilesToPush = pcPrep.FstabFilesToPush;
                patchedVendorImagePath = pcPrep.PatchedVendorImagePath;
                dfeNeededButSkipped = pcPrep.DfeNeededButSkipped;
                Report(progress, CustomRomInstallStage.Validating, 9, pcPrep.Message);
            }

            Task<RomBootImages?>? bootPrep = package.HasPayloadBin
                ? _finalizer.PrepareBootImagesAsync(zipPath, cancellationToken: cancellationToken)
                : null;

            if (options.FlashDfeZip)
            {
                var dfePath = DfeZipResolver.ResolveDfeZip(options.TargetAndroidMajor, options.DfeZipPath);
                if (string.IsNullOrWhiteSpace(dfePath))
                {
                    var dir = DfeZipResolver.GetBundledDfeDirectory();
                    return Fail(
                        "DFE zip bulunamadı.\n\n" +
                        $"Android {options.TargetAndroidMajor?.ToString() ?? "?"} için Disable Force Encrypt zip seçin " +
                        $"veya `{dir}` klasörüne koyun.\n\nAlternatif: yalnızca Format Data ile devam edin.");
                }

                Report(progress, CustomRomInstallStage.Wiping, 11,
                    $"Güvenlik düzenlemesi kuruluyor: {Path.GetFileName(dfePath)}…");
                var dfeResult = await _recovery
                    .InstallRecoveryZipsAsync([dfePath], RecoveryZipInstallMethod.TWRPAdbInstall, cancellationToken)
                    .ConfigureAwait(false);
                if (!dfeResult.Success)
                {
                    Report(progress, CustomRomInstallStage.Failed, 0, dfeResult.Message);
                    return dfeResult;
                }

                Report(progress, CustomRomInstallStage.Wiping, 13, "Kurulum modu yenileniyor…");
                var afterDfe = await RestoreTwrpSessionAsync(options, progress, 13, cancellationToken)
                    .ConfigureAwait(false);
                if (afterDfe is not null)
                    return afterDfe;
            }

            if (options.WipeCache || options.WipeDalvik || options.FormatData)
            {
                if (options.WipeCache && !session.FastbootCacheWiped)
                {
                    Report(progress, CustomRomInstallStage.Wiping, 12, "Önbellekler temizleniyor…");
                    await TryRecoveryCommandAsync("twrp wipe cache", cancellationToken).ConfigureAwait(false);
                }

                if (options.WipeDalvik)
                {
                    Report(progress, CustomRomInstallStage.Wiping, 16, "Dalvik önbelleği temizleniyor…");
                    await TryRecoveryCommandAsync("twrp wipe dalvik", cancellationToken).ConfigureAwait(false);
                }

                if (options.FormatData && !session.FastbootFormatted)
                {
                    Report(progress, CustomRomInstallStage.Wiping, 20, "Telefon temizleniyor (TWRP format)…");
                    var formatResult = await FormatDataViaTwrpAsync(cancellationToken).ConfigureAwait(false);
                    if (!formatResult.Success)
                    {
                        Report(progress, CustomRomInstallStage.Failed, 0, formatResult.Message);
                        return formatResult;
                    }

                    Report(progress, CustomRomInstallStage.Wiping, 24,
                        "Temizlik tamam — kurulum modu yenileniyor…");
                    var afterFormat = await RestoreTwrpSessionAsync(options, progress, 24, cancellationToken)
                        .ConfigureAwait(false);
                    if (afterFormat is not null)
                        return afterFormat;
                }
                else if (options.FormatData && session.FastbootFormatted)
                {
                    Report(progress, CustomRomInstallStage.Wiping, 22, "🗑️ Telefon fastboot'tan temizlendi");
                }
            }

            Report(progress, CustomRomInstallStage.EnteringSideload, 28, "Kurulum moduna geçiliyor…");
            await TryRecoveryCommandAsync("twrp sideload", cancellationToken).ConfigureAwait(false);
            await Task.Delay(2500, cancellationToken).ConfigureAwait(false);
            _ = await _adb.WaitForAdbDeviceAsync(TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);

            Report(progress, CustomRomInstallStage.Sideloading, 30, "Sistem kuruluyor…");
            var sideload = await _adb.SideloadAsync(
                    sideloadZipPath,
                    new Progress<SideloadProgress>(p =>
                    {
                        var mapped = 30 + (int)Math.Round(p.Percent * 0.70);
                        Report(
                            progress,
                            CustomRomInstallStage.Sideloading,
                            Math.Clamp(mapped, 30, 100),
                            string.IsNullOrWhiteSpace(p.Line) ? $"Sistem kuruluyor %{p.Percent}" : p.Line);
                    }),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!sideload.Success)
            {
                Report(progress, CustomRomInstallStage.Failed, 0, sideload.Message);
                return sideload;
            }

            var recoveryReady = await PostSideloadRecoveryHelper
                .EnsureRecoveryShellAsync(_adb, _logger, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!recoveryReady.Success)
            {
                Report(progress, CustomRomInstallStage.Failed, 0, recoveryReady.Message);
                return Fail(recoveryReady.Message);
            }

            if (fstabFilesToPush.Count > 0 && string.IsNullOrWhiteSpace(patchedVendorImagePath))
            {
                Report(progress, CustomRomInstallStage.PatchingFstab, 89,
                    "Güvenlik ayarları yükleniyor (adb push)…");

                var pushResult = await FstabPushPatcher
                    .PushPreparedFilesAsync(_adb, fstabFilesToPush, _logger, cancellationToken)
                    .ConfigureAwait(false);

                if (!pushResult.Success)
                {
                    var pushMsg = pushResult.Message + Environment.NewLine +
                                  "Manuel: fastbootd → fastboot flash vendor patched_vendor.img";
                    if (options.RequireFstabPatch)
                    {
                        Report(progress, CustomRomInstallStage.Failed, 0, pushMsg);
                        return Fail(pushMsg);
                    }

                    Report(progress, CustomRomInstallStage.PatchingFstab, 92, pushMsg);
                }
                else
                {
                    Report(progress, CustomRomInstallStage.PatchingFstab, 94, pushResult.Message);
                }
            }
            else if (!string.IsNullOrWhiteSpace(patchedVendorImagePath))
            {
                Report(progress, CustomRomInstallStage.PatchingFstab, 89,
                    "Güvenlik ayarları fastbootd vendor flash ile yazılacak (sideload sonrası)…");
            }
            else if (options.PatchFstabDisableForceEncrypt && !skipDeviceFstabPatch)
            {
                Report(progress, CustomRomInstallStage.PatchingFstab, 89, "Güvenlik ayarları düzenleniyor…");

                var fstabResult = await FstabForceEncryptPatcher
                    .PatchViaRecoveryShellAsync(_adb, _logger, cancellationToken)
                    .ConfigureAwait(false);

                if (!fstabResult.Success)
                {
                    var fstabMsg =
                        fstabResult.Message + Environment.NewLine +
                        "Manuel: TWRP → Mount System/Vendor → sed -i 's/forceencrypt/encryptable/g' /vendor/etc/fstab.qcom";
                    if (options.RequireFstabPatch)
                    {
                        Report(progress, CustomRomInstallStage.Failed, 0, fstabMsg);
                        return Fail(fstabMsg);
                    }

                    Report(progress, CustomRomInstallStage.PatchingFstab, 92, fstabMsg);
                }
                else
                {
                    Report(progress, CustomRomInstallStage.PatchingFstab, 94, fstabResult.Message);
                }
            }

            else if (options.PatchFstabDisableForceEncrypt && skipDeviceFstabPatch)
            {
                Report(progress, CustomRomInstallStage.PatchingFstab, 92,
                    dfeNeededButSkipped
                        ? "⚠️ DFE uygulanamadı (EROFS vendor yazılamadı). ROM kurulumu devam ediyor."
                        : fstabPatchedOnPc
                            ? "✅ fstab PC'de hazırlandı."
                            : "✅ vendor PC'de incelendi — DFE bayrağı yok.");
            }

            var twrpFlashed = false;
            if (package.HasPayloadBin)
            {
                RomBootImages? prepared = null;
                if (bootPrep is not null)
                {
                    try
                    {
                        prepared = await bootPrep.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "[CustomROM] Boot imaj hazırlığı başarısız — finalize sırasında tekrar denenecek");
                    }
                }

                var finalize = await _finalizer
                    .FinalizeAfterSideloadAsync(
                        zipPath,
                        prepared,
                        _fastboot,
                        _adb,
                        _logger,
                        patchedVendorImagePath,
                        options.TwrpImagePath,
                        _recovery,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!finalize.Success)
                {
                    Report(progress, CustomRomInstallStage.Failed, 0, finalize.Message);
                    return finalize;
                }

                twrpFlashed = finalize.Message.Contains("TWRP yüklendi", StringComparison.Ordinal);
            }

            var twrpNote = twrpFlashed ? " TWRP en sonda yazıldı." : "";

            Report(progress, CustomRomInstallStage.Completed, 100,
                "🎉 Kurulum tamam!" + twrpNote);
            return new DeviceToolResult
            {
                Success = true,
                Message = (!string.IsNullOrWhiteSpace(patchedVendorImagePath)
                    ? "Custom ROM kuruldu — DFE vendor fastbootd ile yazıldı, sistem dosyaları tamamlandı."
                    : fstabFilesToPush.Count > 0
                        ? "Custom ROM kuruldu — fstab adb push ile yüklendi, sistem dosyaları yazıldı."
                        : options.PatchFstabDisableForceEncrypt
                            ? fstabPatchedOnPc
                                ? "Custom ROM kuruldu — fstab PC'de hazırlandı, sistem dosyaları yazıldı."
                                : "Custom ROM kuruldu — güvenlik ayarları düzenlendi, sistem dosyaları yazıldı."
                            : "Custom ROM kuruldu — sistem dosyaları yazıldı.")
                    + twrpNote
            };
        }
        catch (OperationCanceledException)
        {
            Report(progress, CustomRomInstallStage.Failed, 0, "İşlem iptal edildi.");
            return Fail("İşlem iptal edildi.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[CustomROM] Install failed");
            Report(progress, CustomRomInstallStage.Failed, 0, ex.Message);
            return Fail(ex.Message);
        }
    }

    private sealed record RecoverySessionPrep(
        DeviceToolResult? Error,
        bool FastbootFormatted,
        bool FastbootCacheWiped);

    private async Task<RecoverySessionPrep> PrepareRecoverySessionAsync(
        string zipPath,
        CustomRomInstallOptions options,
        IProgress<CustomRomInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var fastbootFormatted = false;
        var fastbootCacheWiped = false;

        switch (options.TwrpLaunch)
        {
            case TwrpLaunchStrategy.AlreadyInRecovery:
                Report(progress, CustomRomInstallStage.FlashingTwrp, 5, "Cihaz recovery'de…");
                if (options.DisableAvbVerity || options.FormatData || options.WipeCache)
                {
                    var fromRecovery = await WipeViaFastbootThenBootTwrpAsync(
                            zipPath, options, progress, cancellationToken, requireTwrpImage: true)
                        .ConfigureAwait(false);
                    if (fromRecovery.Error is not null)
                        return fromRecovery;
                    return fromRecovery;
                }

                await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                return new RecoverySessionPrep(null, false, false);

            case TwrpLaunchStrategy.BootOnceFromFastboot:
            {
                var serial = await EnsureFastbootSerialAsync(options, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (serial is null)
                {
                    return new RecoverySessionPrep(
                        Fail(
                            "Fastboot cihazı bulunamadı.\n\n" +
                            "Telefonu bootloader ekranında USB ile bağlayın veya TWRP'deyseniz kurulumu tekrar başlatın (otomatik fastboot'a geçer)."),
                        false,
                        false);
                }

                var avb = await FlashAvbIfRequestedAsync(serial, zipPath, options, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (avb is not null)
                    return new RecoverySessionPrep(avb, false, false);

                if (options.FormatData || options.WipeCache)
                {
                    Report(progress, CustomRomInstallStage.Wiping, 8,
                        "Telefon fastboot'tan temizleniyor (Format Data)…");
                    var wipe = await FastbootDataWiper
                        .WipeForFreshInstallAsync(
                            serial,
                            options.FormatData,
                            options.WipeCache,
                            _logger,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!wipe.Success && options.FormatData)
                    {
                        return new RecoverySessionPrep(
                            Fail($"Fastboot temizlik başarısız:\n{wipe.Message}"),
                            false,
                            false);
                    }

                    fastbootFormatted = wipe.UserdataErased;
                    fastbootCacheWiped = wipe.CacheErased;
                    if (wipe.Success)
                    {
                        Report(progress, CustomRomInstallStage.Wiping, 10,
                            fastbootFormatted
                                ? $"🗑️ {wipe.Message}"
                                : wipe.Message);
                    }
                }

                var bootErr = await BootTwrpOnceFromFastbootSerialAsync(
                        serial, options, progress, cancellationToken)
                    .ConfigureAwait(false);
                return bootErr is not null
                    ? new RecoverySessionPrep(bootErr, fastbootFormatted, fastbootCacheWiped)
                    : new RecoverySessionPrep(null, fastbootFormatted, fastbootCacheWiped);
            }

            case TwrpLaunchStrategy.FlashPermanent:
            {
                var serial = await EnsureFastbootSerialAsync(options, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (serial is null)
                {
                    return new RecoverySessionPrep(
                        Fail("Kalıcı TWRP için fastboot cihazı gerekli."),
                        false,
                        false);
                }

                var avb = await FlashAvbIfRequestedAsync(serial, zipPath, options, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (avb is not null)
                    return new RecoverySessionPrep(avb, false, false);

                if (options.FormatData || options.WipeCache)
                {
                    Report(progress, CustomRomInstallStage.Wiping, 7, "Telefon fastboot'tan temizleniyor…");
                    var wipe = await FastbootDataWiper
                        .WipeForFreshInstallAsync(serial, options.FormatData, options.WipeCache, _logger, cancellationToken)
                        .ConfigureAwait(false);
                    if (!wipe.Success && options.FormatData)
                        return new RecoverySessionPrep(Fail(wipe.Message), false, false);

                    fastbootFormatted = wipe.UserdataErased;
                    fastbootCacheWiped = wipe.CacheErased;
                }

                var permErr = await FlashPermanentTwrpAsync(options, progress, cancellationToken)
                    .ConfigureAwait(false);
                return permErr is not null
                    ? new RecoverySessionPrep(permErr, fastbootFormatted, fastbootCacheWiped)
                    : new RecoverySessionPrep(null, fastbootFormatted, fastbootCacheWiped);
            }

            default:
                return new RecoverySessionPrep(Fail("Geçersiz TWRP giriş modu."), false, false);
        }
    }

    private async Task<RecoverySessionPrep> WipeViaFastbootThenBootTwrpAsync(
        string zipPath,
        CustomRomInstallOptions options,
        IProgress<CustomRomInstallProgress>? progress,
        CancellationToken cancellationToken,
        bool requireTwrpImage)
    {
        Report(progress, CustomRomInstallStage.Wiping, 6, "Fastboot'a geçiliyor…");
        await RebootBootloaderViaAdbAsync(cancellationToken).ConfigureAwait(false);

        var serial = await WaitForFastbootSerialAsync(TimeSpan.FromSeconds(120), cancellationToken)
            .ConfigureAwait(false);
        if (serial is null)
        {
            return new RecoverySessionPrep(
                Fail("Fastboot'a geçilemedi. Güç + ses ile bootloader ekranına alın."),
                false,
                false);
        }

        var avb = await FlashAvbIfRequestedAsync(serial, zipPath, options, progress, cancellationToken)
            .ConfigureAwait(false);
        if (avb is not null)
            return new RecoverySessionPrep(avb, false, false);

        if (options.FormatData || options.WipeCache)
        {
            Report(progress, CustomRomInstallStage.Wiping, 8, "Telefon fastboot'tan temizleniyor…");
            var wipe = await FastbootDataWiper
                .WipeForFreshInstallAsync(serial, options.FormatData, options.WipeCache, _logger, cancellationToken)
                .ConfigureAwait(false);
            if (!wipe.Success && options.FormatData)
                return new RecoverySessionPrep(Fail(wipe.Message), false, false);

            if (requireTwrpImage)
            {
                if (string.IsNullOrWhiteSpace(options.TwrpImagePath) || !File.Exists(options.TwrpImagePath))
                {
                    return new RecoverySessionPrep(
                        Fail("Format sonrası TWRP başlatmak için .img dosyası seçin."),
                        wipe.UserdataErased,
                        wipe.CacheErased);
                }

                var bootErr = await BootTwrpOnceFromFastbootSerialAsync(serial, options, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (bootErr is not null)
                    return new RecoverySessionPrep(bootErr, wipe.UserdataErased, wipe.CacheErased);
            }

            return new RecoverySessionPrep(null, wipe.UserdataErased, wipe.CacheErased);
        }

        if (requireTwrpImage)
        {
            if (!string.IsNullOrWhiteSpace(options.TwrpImagePath) && File.Exists(options.TwrpImagePath))
            {
                var bootOnly = await BootTwrpOnceFromFastbootSerialAsync(serial, options, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (bootOnly is not null)
                    return new RecoverySessionPrep(bootOnly, false, false);

                return new RecoverySessionPrep(null, false, false);
            }

            Report(progress, CustomRomInstallStage.FlashingTwrp, 8, "AVB kapatıldı — recovery'ye dönülüyor…");
            var reboot = await FastbootDeviceProbe
                .RebootAsync(serial, "recovery", cancellationToken)
                .ConfigureAwait(false);
            if (!reboot.Success)
            {
                return new RecoverySessionPrep(
                    Fail("AVB kapatıldı ancak recovery'ye dönülemedi. TWRP .img seçip tekrar deneyin."),
                    false,
                    false);
            }

            await Task.Delay(8000, cancellationToken).ConfigureAwait(false);
        }

        return new RecoverySessionPrep(null, false, false);
    }

    private async Task<DeviceToolResult?> FlashAvbIfRequestedAsync(
        string serial,
        string zipPath,
        CustomRomInstallOptions options,
        IProgress<CustomRomInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!options.DisableAvbVerity)
            return null;

        Report(progress, CustomRomInstallStage.DisablingAvb, 6,
            "Dijital imza kontrolü kapatılıyor (vbmeta --disable-verity)…");

        var flashProgress = new Progress<FlashingProgressReport>(p =>
            Report(progress, CustomRomInstallStage.DisablingAvb, 7, p.Message));

        var runner = new FastbootFlashRunner();
        var result = await CustomRomAvbDisableStep
            .RunAsync(runner, serial, zipPath, options.StockVbmetaImagePath, flashProgress, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            Report(progress, CustomRomInstallStage.Failed, 0, result.Message);
            return Fail(result.Message);
        }

        _logger.Information("[CustomROM] AVB disable: {Msg}", result.Message);
        Report(progress, CustomRomInstallStage.DisablingAvb, 8, "✅ AVB / dm-verity kapatıldı");
        return null;
    }

    private async Task<string?> EnsureFastbootSerialAsync(
        CustomRomInstallOptions options,
        IProgress<CustomRomInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var serial = await _fastboot.GetFirstSerialAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(serial))
            return serial;

        var device = _adb.SelectedDevice
                     ?? await _adb.WaitForAdbDeviceAsync(TimeSpan.FromSeconds(8), cancellationToken)
                         .ConfigureAwait(false);
        if (device is null)
            return null;

        Report(progress, CustomRomInstallStage.FlashingTwrp, 5, "Fastboot'a geçiliyor…");
        var reboot = await _deviceTools.RebootAsync(DeviceRebootMode.Bootloader, cancellationToken)
            .ConfigureAwait(false);
        if (!reboot.Success)
        {
            try
            {
                await _adb.ExecuteShellAsync("reboot bootloader", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[CustomROM] adb reboot bootloader failed");
            }
        }

        await Task.Delay(6000, cancellationToken).ConfigureAwait(false);
        return await WaitForFastbootSerialAsync(TimeSpan.FromSeconds(120), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RebootBootloaderViaAdbAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reboot = await _deviceTools.RebootAsync(DeviceRebootMode.Bootloader, cancellationToken)
                .ConfigureAwait(false);
            if (!reboot.Success)
                await _adb.ExecuteShellAsync("reboot bootloader", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[CustomROM] reboot bootloader failed");
        }

        await Task.Delay(6000, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DeviceToolResult?> BootTwrpOnceFromFastbootSerialAsync(
        string serial,
        CustomRomInstallOptions options,
        IProgress<CustomRomInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.TwrpImagePath) || !File.Exists(options.TwrpImagePath))
        {
            return Fail(
                "TWRP .img seçilmedi.\n\nÖnerilen: fastboot boot ile geçici TWRP — Format Data fastboot'tan yapıldıktan sonra sideload.");
        }

        var sizeMb = new FileInfo(options.TwrpImagePath).Length / (1024 * 1024);
        Report(progress, CustomRomInstallStage.FlashingTwrp, 6,
            $"Geçici TWRP açılıyor (fastboot boot, ~{sizeMb} MB) — kalıcı yazılmaz…");

        using var bootTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bootTimeout.CancelAfter(TimeSpan.FromMinutes(4));

        DeviceToolResult boot;
        try
        {
            boot = await FastbootDeviceProbe
                .BootImageAsync(serial, options.TwrpImagePath, bootTimeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(
                "Geçici TWRP zaman aşımı (fastboot boot).\n\n" +
                "USB kablo/port değiştirin veya telefonda hâlâ FASTBOOT yazıyorsa:\n" +
                $"fastboot boot \"{options.TwrpImagePath}\"\n\n" +
                "TWRP açılınca manager'da «Zaten TWRP'deyim» ile devam edin.");
        }

        if (!boot.Success)
        {
            Report(progress, CustomRomInstallStage.Failed, 0, boot.Message);
            return boot;
        }

        Report(progress, CustomRomInstallStage.FlashingTwrp, 8, "TWRP bekleniyor (ADB)…");
        var twrp = await WaitForTwrpAdbAsync(TimeSpan.FromSeconds(90), cancellationToken)
            .ConfigureAwait(false);
        if (twrp is null)
        {
            return Fail(
                "fastboot boot gönderildi ama TWRP ADB görünmedi.\n\n" +
                "• Telefon TWRP ana ekranındaysa USB takılı kalsın → Yenile\n" +
                "• Hâlâ FASTBOOT ekranındaysa USB değiştirip tekrar: fastboot boot twrp.img\n" +
                "• TWRP açılınca «Zaten TWRP'deyim» seçip kuruluma devam edin");
        }

        Report(progress, CustomRomInstallStage.FlashingTwrp, 9, "Kurulum modu hazır (geçici TWRP).");
        return null;
    }

    private async Task<DeviceToolResult?> FlashPermanentTwrpAsync(
        CustomRomInstallOptions options,
        IProgress<CustomRomInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.TwrpImagePath) || !File.Exists(options.TwrpImagePath))
            return Fail("Kalıcı TWRP yazımı için .img dosyası seçin.");

        Report(progress, CustomRomInstallStage.FlashingTwrp, 6, "Kalıcı kurulum modu yazılıyor…");
        var twrpFlash = await _recovery
            .FlashRecoveryImageAsync(options.TwrpImagePath, options.PermanentTwrpFlashMethod, cancellationToken)
            .ConfigureAwait(false);
        if (!twrpFlash.Success)
        {
            Report(progress, CustomRomInstallStage.Failed, 0, twrpFlash.Message);
            return twrpFlash;
        }

        Report(progress, CustomRomInstallStage.FlashingTwrp, 8, "Recovery'ye yeniden başlatılıyor…");
        var fbSerial = await _fastboot.GetFirstSerialAsync(cancellationToken).ConfigureAwait(false);
        var reboot = !string.IsNullOrWhiteSpace(fbSerial)
            ? await FastbootDeviceProbe.RebootAsync(fbSerial, "recovery", cancellationToken).ConfigureAwait(false)
            : await _deviceTools.RebootAsync(DeviceRebootMode.Recovery, cancellationToken).ConfigureAwait(false);

        if (!reboot.Success)
        {
            return Fail(
                "TWRP yazıldı ancak recovery'ye geçilemedi.\n\n" +
                reboot.Message + "\n\nÖneri: «Geçici fastboot boot» modunu kullanın.");
        }

        await Task.Delay(10000, cancellationToken).ConfigureAwait(false);
        return null;
    }

    private async Task<DeviceToolResult> InstallViaFastbootEngineAsync(
        string zipPath,
        CustomRomInstallOptions options,
        IProgress<CustomRomInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, CustomRomInstallStage.Validating, 5, "Fastboot payload motoru hazırlanıyor…");

        var serial = _adb.SelectedDevice?.Serial;
        if (string.IsNullOrWhiteSpace(serial))
            serial = await _fastboot.GetFirstSerialAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(serial))
        {
            var path = _fastboot.ResolvedExecutablePath ?? "(bulunamadı)";
            return Fail(
                $"Fastboot cihazı bulunamadı.\n\nTelefon fastboot/bootloader ekranında USB ile bağlı olsun.\nBat dosyanız çalışıyorsa Ayarlar → fastboot.exe yolunu aynı klasöre ayarlayın.\nŞu an çözümlenen yol: {path}");
        }

        Report(progress, CustomRomInstallStage.Validating, 6, $"Fastboot cihaz: {serial}");
        var flashProgress = new Progress<FlashingProgressReport>(p =>
        {
            var stage = p.Stage switch
            {
                CustomRomFlashStage.Extracting => CustomRomInstallStage.Extracting,
                CustomRomFlashStage.DumpingPayload => CustomRomInstallStage.DumpingPayload,
                CustomRomFlashStage.EnteringFastbootd => CustomRomInstallStage.EnteringFastbootd,
                CustomRomFlashStage.Flashing => CustomRomInstallStage.FastbootFlashing,
                CustomRomFlashStage.Completed => CustomRomInstallStage.Completed,
                CustomRomFlashStage.Failed => CustomRomInstallStage.Failed,
                CustomRomFlashStage.Cancelled => CustomRomInstallStage.Failed,
                _ => CustomRomInstallStage.FastbootFlashing
            };

            Report(progress, stage, p.Percent, p.Message);
        });

        var request = new CustomRomFlashRequest
        {
            ZipPath = zipPath,
            DeviceSerial = serial,
            DeviceCodename = options.DeviceCodename,
            WipeUserData = options.FormatData,
            EnsureBootloaderAsync = async ct =>
            {
                var reboot = await _deviceTools.RebootAsync(DeviceRebootMode.Bootloader, ct).ConfigureAwait(false);
                if (!reboot.Success)
                    throw new InvalidOperationException(reboot.Message);
            }
        };

        try
        {
            var result = await _flashingEngine
                .FlashAsync(request, flashProgress, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                Report(progress, CustomRomInstallStage.Failed, 0, result.Message);
                return Fail(result.Message);
            }

            Report(progress, CustomRomInstallStage.Completed, 100, result.Message);
            var message = options.SafeUnmodifiedInstall
                ? "Güvenli custom ROM yüklendi. TWRP yazılmadı; ROM zip'ine fstab / DFE / Magisk eklenmedi. Telefon yeniden başlatılıyor."
                : result.Message;
            return new DeviceToolResult { Success = true, Message = message };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[CustomROM] Fastboot engine failed");
            Report(progress, CustomRomInstallStage.Failed, 0, ex.Message);
            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// Format/DFE sonrası oturum düşer. Geçici TWRP (fastboot boot) fastboot'a döner — yeniden boot edilir.
    /// </summary>
    private async Task<DeviceToolResult?> RestoreTwrpSessionAsync(
        CustomRomInstallOptions options,
        IProgress<CustomRomInstallProgress>? progress,
        int percent,
        CancellationToken cancellationToken)
    {
        if (options.TwrpLaunch == TwrpLaunchStrategy.BootOnceFromFastboot)
        {
            Report(progress, CustomRomInstallStage.Wiping, percent, "Kurulum modu yenileniyor…");
            await Task.Delay(4000, cancellationToken).ConfigureAwait(false);

            var fbSerial = await WaitForFastbootSerialAsync(TimeSpan.FromSeconds(90), cancellationToken)
                .ConfigureAwait(false);
            if (fbSerial is not null)
            {
                if (string.IsNullOrWhiteSpace(options.TwrpImagePath) || !File.Exists(options.TwrpImagePath))
                    return Fail("Kurulum modu imajı bulunamadı; oturumu yenilemek için gerekli.");

                var boot = await FastbootDeviceProbe
                    .BootImageAsync(fbSerial, options.TwrpImagePath, cancellationToken)
                    .ConfigureAwait(false);
                if (!boot.Success)
                {
                    Report(progress, CustomRomInstallStage.Failed, 0, boot.Message);
                    return boot;
                }

                await Task.Delay(10000, cancellationToken).ConfigureAwait(false);
                var adbAfterBoot = await WaitForTwrpAdbAsync(TimeSpan.FromSeconds(75), cancellationToken)
                    .ConfigureAwait(false);
                if (adbAfterBoot is null)
                {
                    return Fail(
                        "Kurulum modu yeniden açıldı ama ADB gelmedi.\n\n" +
                        "USB takılı kalın ve Yenile ile tekrar deneyin.");
                }

                return null;
            }

            var stillInRecovery = await WaitForTwrpAdbAsync(TimeSpan.FromSeconds(8), cancellationToken)
                .ConfigureAwait(false);
            if (stillInRecovery is not null)
                return null;

            return Fail(
                "Format/DFE sonrası cihaz fastboot'ta görünmüyor.\n\n" +
                "Telefonu bootloader ekranında USB ile takılı tutun.");
        }

        var quickAdb = await WaitForTwrpAdbAsync(TimeSpan.FromSeconds(12), cancellationToken)
            .ConfigureAwait(false);
        if (quickAdb is not null)
            return null;

        // Kalıcı TWRP veya zaten recovery'de: recovery reboot dene.
        await RebootRecoveryViaAdbAsync(cancellationToken).ConfigureAwait(false);
        var fbAfterReboot = await WaitForFastbootSerialAsync(TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);
        if (fbAfterReboot is not null)
        {
            await FastbootDeviceProbe.RebootAsync(fbAfterReboot, "recovery", cancellationToken)
                .ConfigureAwait(false);
            await Task.Delay(8000, cancellationToken).ConfigureAwait(false);
        }

        var recovered = await WaitForTwrpAdbAsync(TimeSpan.FromSeconds(75), cancellationToken)
            .ConfigureAwait(false);
        if (recovered is null)
        {
            return Fail(
                "Recovery ADB gelmedi. TWRP ana menüsünde USB takılı kalsın; sideload'a devam edilemiyor.");
        }

        return null;
    }

    private async Task<string?> WaitForFastbootSerialAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var serial = await _fastboot.GetFirstSerialAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(serial))
                return serial;

            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<DeviceToolResult> FormatDataViaTwrpAsync(CancellationToken cancellationToken)
    {
        try
        {
            var wipe = await RunRecoveryCommandRequiredAsync("twrp wipe data", cancellationToken)
                .ConfigureAwait(false);
            if (!wipe.Success)
                return wipe;

            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
            return await RunRecoveryCommandRequiredAsync("twrp format data", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Fail("Format Data iptal edildi.");
        }
    }

    private async Task RebootRecoveryViaAdbAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _adb.ExecuteShellAsync("reboot recovery", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[CustomROM] reboot recovery failed");
        }

        await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ConnectedDevice?> WaitForRecoveryAdbReadyAsync(
        bool afterFormatData,
        IProgress<CustomRomInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var timeout = afterFormatData ? TimeSpan.FromSeconds(120) : TimeSpan.FromSeconds(90);
        var deadline = DateTime.UtcNow + timeout;
        var adbRestarted = false;
        var offlineLogged = false;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var devices = await _adb.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
            var ready = devices.FirstOrDefault(d => d.IsAdbReady && (d.IsRecovery || d.IsSideload || d.IsOnline));
            if (ready is not null)
            {
                _adb.SelectDevice(ready);
                return ready;
            }

            if (devices.Any(d => d.State.Contains("offline", StringComparison.OrdinalIgnoreCase)))
            {
                if (!offlineLogged && afterFormatData)
                {
                    offlineLogged = true;
                    Report(progress, CustomRomInstallStage.Validating, 10,
                        "USB offline — Format Data sonrası normal, yeniden bağlanıyor…");
                }

                if (!adbRestarted && DateTime.UtcNow + TimeSpan.FromSeconds(90) < deadline)
                {
                    adbRestarted = true;
                    await TryRestartAdbTransportAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        }

        return await _adb.WaitForAdbDeviceAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
    }

    private async Task TryRestartAdbTransportAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _adb.StopAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(800, cancellationToken).ConfigureAwait(false);
            await _adb.StartAsync(cancellationToken).ConfigureAwait(false);
            _logger.Information("[CustomROM] ADB yeniden başlatıldı (Format sonrası offline)");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[CustomROM] ADB restart after format failed");
        }
    }

    private async Task<ConnectedDevice?> WaitForTwrpAdbAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        await _adb.WaitForAdbDeviceAsync(timeout, cancellationToken).ConfigureAwait(false);

    private Task<ConnectedDevice?> WaitForTwrpAdbAsync(CancellationToken cancellationToken) =>
        WaitForTwrpAdbAsync(TimeSpan.FromSeconds(75), cancellationToken);

    private async Task<DeviceToolResult> RunRecoveryCommandRequiredAsync(
        string command,
        CancellationToken cancellationToken)
    {
        try
        {
            var output = await _adb.ExecuteShellAsync(command, cancellationToken).ConfigureAwait(false);
            _logger.Information("[CustomROM] {Command} => {Output}", command, output.Trim());

            if (output.Contains("Error", StringComparison.OrdinalIgnoreCase)
                && !output.Contains("Done processing file", StringComparison.OrdinalIgnoreCase))
            {
                return Fail($"{command} başarısız görünüyor:\n{Truncate(output, 600)}");
            }

            return new DeviceToolResult { Success = true, Message = output.Trim() };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[CustomROM] {Command} failed", command);
            return Fail($"{command} hata: {ex.Message}");
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private async Task TryRecoveryCommandAsync(string command, CancellationToken cancellationToken)
    {
        try
        {
            var output = await _adb.ExecuteShellAsync(command, cancellationToken).ConfigureAwait(false);
            _logger.Information("[CustomROM] {Command} => {Output}", command, output.Trim());
        }
        catch (Exception ex)
        {
            // twrp sideload cihazı ADB'den düşürür; bu beklenen bir kopuştur.
            _logger.Warning(ex, "[CustomROM] {Command} sonlandı (sideload geçişi olabilir)", command);
        }
    }

    private static IProgress<FlashingProgressReport>? MapPcPrepareProgress(
        IProgress<CustomRomInstallProgress>? progress) =>
        progress is null
            ? null
            : new Progress<FlashingProgressReport>(p =>
                Report(progress, CustomRomInstallStage.Validating, Math.Clamp(p.Percent, 8, 18), p.Message));

    private static void Report(
        IProgress<CustomRomInstallProgress>? progress,
        CustomRomInstallStage stage,
        int percent,
        string message)
    {
        progress?.Report(new CustomRomInstallProgress
        {
            Stage = stage,
            Percent = Math.Clamp(percent, 0, 100),
            Message = message
        });
    }

    private static DeviceToolResult Fail(string message) => new() { Success = false, Message = message };
}
