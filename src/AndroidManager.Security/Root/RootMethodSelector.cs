using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;

namespace AndroidManager.Security.Root;

public static class RootMethodSelector
{
    public static RootCompatibilityLevel EvaluateCompatibility(DeviceProfile profile)
    {
        if (profile.ApiLevel < 21)
            return RootCompatibilityLevel.Unsupported;

        if (!profile.BootloaderUnlocked)
            return RootCompatibilityLevel.Unsupported;

        if (profile.IsRooted)
            return RootCompatibilityLevel.Supported;

        if (profile.Architecture.Contains("arm64", StringComparison.OrdinalIgnoreCase) &&
            profile.ApiLevel >= 26)
            return RootCompatibilityLevel.LikelySupported;

        return RootCompatibilityLevel.Experimental;
    }

    public static RootMethodRecommendation Recommend(DeviceProfile profile, IEnumerable<IRootMethodProvider> providers)
    {
        var supported = providers
            .Where(p => p.SupportsDevice(profile))
            .ToList();

        var method = SelectBestMethod(profile, supported);
        var provider = supported.FirstOrDefault(p => p.MethodType == method);

        var patchTarget = profile.HasInitBoot && profile.ApiLevel >= 33
            ? "init_boot"
            : profile.HasVendorBoot && profile.ApiLevel >= 30
                ? "vendor_boot"
                : "boot";

        var level = provider is null
            ? RootCompatibilityLevel.Unsupported
            : EvaluateCompatibility(profile);

        var warnings = new List<string>();
        if (!profile.BootloaderUnlocked)
            warnings.Add("Bootloader kilitli — OEM unlock gerekli");
        if (profile.IsAbPartition)
            warnings.Add("A/B cihaz — doğru slot için yedek alın");
        if (level == RootCompatibilityLevel.Experimental)
            warnings.Add("Cihaz profili sınırlı — manuel doğrulama gerekli");

        return new RootMethodRecommendation
        {
            Method = method,
            Level = level,
            DisplayName = method switch
            {
                RootMethodType.Magisk => "Magisk",
                RootMethodType.KernelSU => "KernelSU",
                RootMethodType.Apatch => "APatch",
                _ => "Bilinmiyor"
            },
            PatchTarget = patchTarget,
            Summary = BuildSummary(profile, method, patchTarget, level),
            IsRecommended = level is RootCompatibilityLevel.Supported or RootCompatibilityLevel.LikelySupported,
            Warnings = warnings,
            Requirements =
            [
                "Bootloader kilidi açık olmalı",
                "Partition yedeği alınmalı",
                "Magisk/KernelSU/APatch Manager APK",
                "Kullanıcı risk onayı"
            ]
        };
    }

    private static RootMethodType SelectBestMethod(DeviceProfile profile, IReadOnlyList<IRootMethodProvider> supported)
    {
        if (supported.Any(p => p.MethodType == RootMethodType.Magisk))
            return RootMethodType.Magisk;
        if (profile.IsGki && supported.Any(p => p.MethodType == RootMethodType.KernelSU))
            return RootMethodType.KernelSU;
        if (supported.Any(p => p.MethodType == RootMethodType.Apatch))
            return RootMethodType.Apatch;
        return supported.FirstOrDefault()?.MethodType ?? RootMethodType.Unknown;
    }

    private static string BuildSummary(DeviceProfile profile, RootMethodType method, string patchTarget, RootCompatibilityLevel level)
    {
        if (level == RootCompatibilityLevel.Unsupported)
            return "Bu cihaz için otomatik root önerilmiyor — manuel mod gerekli";

        return method switch
        {
            RootMethodType.Magisk => $"Önerilen: Magisk ile {patchTarget} patch",
            RootMethodType.KernelSU => "Önerilen: KernelSU (GKI kernel gerekebilir)",
            RootMethodType.Apatch => "Önerilen: APatch kernel patch",
            _ => "UNKNOWN DEVICE — Manual mode required"
        };
    }
}
