using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;

namespace AndroidManager.Security.Root;

public sealed class RootAnalysisService : IRootAnalysisService
{
    private readonly DeviceProfileAnalyzer _analyzer;
    private readonly RootPreflightService _preflight;
    private readonly IEnumerable<IRootMethodProvider> _providers;

    public RootAnalysisService(
        DeviceProfileAnalyzer analyzer,
        RootPreflightService preflight,
        IEnumerable<IRootMethodProvider> providers)
    {
        _analyzer = analyzer;
        _preflight = preflight;
        _providers = providers;
    }

    public Task<DeviceProfile> AnalyzeDeviceAsync(CancellationToken cancellationToken = default) =>
        _analyzer.AnalyzeAsync(cancellationToken);

    public async Task<RootMethodRecommendation> RecommendMethodAsync(DeviceProfile profile, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return RootMethodSelector.Recommend(profile, _providers);
    }

    public Task<RootPreflightResult> RunPreflightAsync(DeviceProfile profile, RootMethodType method, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var rootDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AndroidManager", "Root");
        var backup = Directory.Exists(rootDir)
            ? Directory.EnumerateFiles(rootDir, "*.img", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        return Task.FromResult(_preflight.Run(profile, method, backup));
    }
}
