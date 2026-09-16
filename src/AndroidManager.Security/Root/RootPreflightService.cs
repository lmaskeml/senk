using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;

namespace AndroidManager.Security.Root;

public sealed class RootPreflightService
{
    public RootPreflightResult Run(DeviceProfile profile, RootMethodType method, string? bootImagePath)
    {
        var checks = new List<RootPreflightCheck>
        {
            new()
            {
                Title = "Cihaz bağlı",
                Passed = !string.IsNullOrWhiteSpace(profile.SerialNumber),
                Detail = profile.SerialNumber,
                IsCritical = true
            },
            new()
            {
                Title = "Bootloader durumu",
                Passed = profile.BootloaderUnlocked,
                Detail = profile.BootloaderUnlocked ? "Kilitsiz" : "Kilitli — unlock gerekli",
                IsCritical = true
            },
            new()
            {
                Title = "Cihaz profili",
                Passed = profile.Compatibility != RootCompatibilityLevel.Unsupported,
                Detail = profile.Compatibility.ToString(),
                IsCritical = true
            },
            new()
            {
                Title = "Root yöntemi",
                Passed = method != RootMethodType.Unknown,
                Detail = method.ToString(),
                IsCritical = true
            },
            new()
            {
                Title = "Partition yedeği",
                Passed = !string.IsNullOrWhiteSpace(bootImagePath) && File.Exists(bootImagePath ?? ""),
                Detail = string.IsNullOrWhiteSpace(bootImagePath) ? "boot/init_boot yedeği yok" : bootImagePath!,
                IsCritical = false
            },
            new()
            {
                Title = "Risk onayı",
                Passed = true,
                Detail = "Kullanıcı onayı UI'da alınmalı",
                IsCritical = true
            }
        };

        var canProceed = checks.Where(c => c.IsCritical).All(c => c.Passed);
        var summary = canProceed
            ? "Ön kontroller geçti — hazırlık adımına devam edilebilir"
            : "Kritik kontroller başarısız — otomatik flash başlatılmamalı";

        return new RootPreflightResult { CanProceed = canProceed, Checks = checks, Summary = summary };
    }
}
