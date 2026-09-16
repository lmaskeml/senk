using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Security.Root.Providers;

public sealed class KernelSuRootProvider : IRootMethodProvider
{
    private readonly IAdbService _adb;
    private readonly ILogger _logger;

    public KernelSuRootProvider(IAdbService adb, ILogger? logger = null)
    {
        _adb = adb;
        _logger = logger ?? Log.ForContext<KernelSuRootProvider>();
    }

    public RootMethodType MethodType => RootMethodType.KernelSU;

    public bool SupportsDevice(DeviceProfile profile) =>
        profile.ApiLevel >= 26 &&
        profile.Architecture.Contains("arm64", StringComparison.OrdinalIgnoreCase);

    public async Task<string> DownloadManagerApkAsync(string targetDir, IProgress<int>? progress, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(targetDir);
        var dest = Path.Combine(targetDir, "KernelSU.apk");
        if (File.Exists(dest))
        {
            progress?.Report(100);
            return dest;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AndroidManager/1.0");
        var json = await http.GetStringAsync(
            "https://api.github.com/repos/tiann/KernelSU/releases/latest", cancellationToken).ConfigureAwait(false);

        var key = "\"browser_download_url\": \"";
        var start = json.IndexOf(key, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException("KernelSU APK URL bulunamadı");
        start += key.Length;
        var end = json.IndexOf('"', start);
        var apkUrl = json[start..end];

        using var response = await http.GetAsync(apkUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = File.Create(dest);
        await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        progress?.Report(100);
        return dest;
    }

    public Task<string> WaitForPatchResultAsync(
        string serial,
        string remoteBootPath,
        string workingDir,
        IProgress<RootStep>? progress,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new RootStep
        {
            Order = 1,
            Title = "KernelSU",
            Description = "KernelSU cihaza özel kernel gerektirir — otomatik patch desteklenmiyor",
            Status = RootStepStatus.Skipped,
            WarningMessage = "Kernel image'ı KernelSU Manager ile manuel hazırlayın"
        });

        var path = Path.Combine(workingDir, "kernelsu_boot.img");
        if (File.Exists(remoteBootPath))
            File.Copy(remoteBootPath, path, overwrite: true);
        return Task.FromResult(path);
    }

    public async Task<bool> VerifyRootAsync(string serial, CancellationToken cancellationToken = default)
    {
        var pkg = await _adb.ExecuteShellAsync("pm list packages me.weishu.kernelsu", cancellationToken).ConfigureAwait(false);
        var su = await _adb.ExecuteShellAsync("su -c id 2>/dev/null", cancellationToken).ConfigureAwait(false);
        return pkg.Contains("kernelsu", StringComparison.OrdinalIgnoreCase) && su.Contains("uid=0");
    }
}
