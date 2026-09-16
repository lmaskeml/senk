using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface ICustomRomWizardService
{
    Task<CustomRomPackageInfo> InspectPackageAsync(string zipPath, CancellationToken cancellationToken = default);

    CustomRomCompatibilityResult EvaluateCompatibility(CustomRomPackageInfo package, string deviceCodename);

    Task<DeviceToolResult> InstallAsync(
        string zipPath,
        CustomRomInstallOptions options,
        IProgress<CustomRomInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
