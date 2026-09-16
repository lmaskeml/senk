using AndroidManager.Core.Abstractions;

namespace AndroidManager.Core.Models;

public enum TwrpLaunchStrategy
{
    /// <summary>Cihaz zaten TWRP/recovery'de — fastboot gerekmez.</summary>
    AlreadyInRecovery = 0,

    /// <summary>fastboot boot — geçici TWRP, kurulum için önerilen (ROM sonrası kendi recovery'sini yazar).</summary>
    BootOnceFromFastboot = 1,

    /// <summary>fastboot flash — kalıcı TWRP; ROM öncesi önerilmez (şifreli data ile TWRP'de takılma riski).</summary>
    FlashPermanent = 2,
}

public enum CustomRomWizardStep
{
    Package = 0,
    Prepare = 1,
    Flash = 2
}

public enum CustomRomCompatibility
{
    Unknown,
    Compatible,
    Unverifiable,
    Incompatible
}

public enum CustomRomInstallStage
{
    Idle,
    Validating,
    DisablingAvb,
    FlashingTwrp,
    Wiping,
    EnteringSideload,
    Sideloading,
    PatchingFstab,
    FinalizingBoot,
    Extracting,
    DumpingPayload,
    EnteringFastbootd,
    FastbootFlashing,
    Completed,
    Failed
}

public sealed class CustomRomPackageInfo
{
    public string FilePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public long SizeBytes { get; init; }
    public bool HasUpdaterScript { get; init; }
    public bool HasUpdateBinary { get; init; }
    public bool HasPayloadBin { get; init; }
    public bool HasDynamicPartitionsList { get; init; }
    public IReadOnlyList<string> ClaimedDevices { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string Summary { get; init; } = "";

    /// <summary>ROM dosya adından tahmin (ör. crDroidAndroid-15.0 → 15).</summary>
    public int? DetectedAndroidMajor { get; init; }

    public string SizeFormatted => SizeBytes switch
    {
        < 1_048_576 => $"{SizeBytes / 1024.0:F1} KB",
        < 1_073_741_824 => $"{SizeBytes / 1_048_576.0:F1} MB",
        _ => $"{SizeBytes / 1_073_741_824.0:F2} GB"
    };
}

public sealed class CustomRomCompatibilityResult
{
    public CustomRomCompatibility Compatibility { get; init; } = CustomRomCompatibility.Unknown;
    public string DeviceCodename { get; init; } = "";
    public IReadOnlyList<string> ClaimedDevices { get; init; } = [];
    public string Reason { get; init; } = "";
    public bool CanInstall => Compatibility is CustomRomCompatibility.Compatible
        or CustomRomCompatibility.Unverifiable;
}

public sealed class CustomRomInstallOptions
{
    public bool WipeCache { get; init; } = true;
    public bool WipeDalvik { get; init; } = true;
    public bool FormatData { get; init; }

    /// <summary>TWRP'ye DFE (Disable Force Encrypt) zip kur — Force Encrypt / şifreli data takılmasını önler.</summary>
    public bool FlashDfeZip { get; init; }

    /// <summary>Boşsa ROM sürümüne göre tools/dfe/ aranır.</summary>
    public string? DfeZipPath { get; init; }

    /// <summary>ROM paketinden veya cihazdan tahmin edilen Android major (15 vb.).</summary>
    public int? TargetAndroidMajor { get; init; }

    /// <summary>ROM sideload sonrası fstab.qcom: forceencrypt → encryptable (DFE zip yerine, önerilen).</summary>
    public bool PatchFstabDisableForceEncrypt { get; init; } = true;

    /// <summary>Fstab patch başarısız olursa kurulumu durdur.</summary>
    public bool RequireFstabPatch { get; init; }

    /// <summary>
    /// TWRP/sideload öncesi:
    /// <c>fastboot flash vbmeta_ab vbmeta.img --disable-verity --disable-verification</c>
    /// Custom ROM ve TWRP stok imza zincirini kırar; AVB açık kalırsa sistem açılmaz.
    /// </summary>
    public bool DisableAvbVerity { get; init; } = true;

    /// <summary>
    /// Stok vbmeta.img veya orijinal ROM klasörü (images\vbmeta.img).
    /// Boşsa payload.bin içinden çıkarılır; o da yoksa AVB-disabled stub.
    /// </summary>
    public string? StockVbmetaImagePath { get; init; }

    /// <summary>
    /// Güvenli yükleme: TWRP yok, ROM zip'ine dokunulmaz (fstab/DFE/Magisk yok).
    /// Yalnızca payload.bin → fastboot/fastbootd.
    /// </summary>
    public bool SafeUnmodifiedInstall { get; init; }

    /// <summary>payload.bin ROM'ları fastboot/fastbootd ile yükle (TWRP sideload yerine).</summary>
    public bool UseFastbootPayloadEngine { get; init; }

    /// <summary>Cihaz kod adı (vili vb.) — flash profili seçimi.</summary>
    public string? DeviceCodename { get; init; }

    /// <summary>TWRP'ye nasıl girilir (kalıcı yazmadan geçici boot önerilir).</summary>
    public TwrpLaunchStrategy TwrpLaunch { get; init; } = TwrpLaunchStrategy.BootOnceFromFastboot;

    public string? TwrpImagePath { get; init; }

    /// <summary>Yalnızca <see cref="TwrpLaunchStrategy.FlashPermanent"/> için.</summary>
    public RecoveryFlashMethod PermanentTwrpFlashMethod { get; init; } = RecoveryFlashMethod.FastbootFlashBoot;
}

public sealed class CustomRomInstallProgress
{
    public CustomRomInstallStage Stage { get; init; } = CustomRomInstallStage.Idle;
    public int Percent { get; init; }
    public string Message { get; init; } = "";
}

public sealed class SideloadProgress
{
    public int Percent { get; init; }
    public string Line { get; init; } = "";
}
