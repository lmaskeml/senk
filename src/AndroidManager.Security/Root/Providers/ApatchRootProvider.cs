using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;

namespace AndroidManager.Security.Root.Providers;

public sealed class ApatchRootProvider : IRootMethodProvider
{
    private readonly IAdbService _adb;

    public ApatchRootProvider(IAdbService adb) => _adb = adb;

    public RootMethodType MethodType => RootMethodType.Apatch;

    public bool SupportsDevice(DeviceProfile profile) => profile.ApiLevel >= 26;

    public async Task<string> DownloadManagerApkAsync(string targetDir, IProgress<int>? progress, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(targetDir);
        var dest = Path.Combine(targetDir, "APatch.apk");
        if (File.Exists(dest))
        {
            progress?.Report(100);
            return dest;
        }

        const string url = "https://github.com/bmax121/APatch/releases/latest/download/APatch.apk";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AndroidManager/1.0");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
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
            Title = "APatch",
            Description = "APatch patch işlemi APatch Manager üzerinden tamamlanmalı",
            Status = RootStepStatus.Skipped,
            WarningMessage = "Otomatik kernel patch sınırlı — APatch UI kullanın"
        });
        return Task.FromResult(Path.Combine(workingDir, "apatch_boot.img"));
    }

    public async Task<bool> VerifyRootAsync(string serial, CancellationToken cancellationToken = default)
    {
        var su = await _adb.ExecuteShellAsync("su -c id 2>/dev/null", cancellationToken).ConfigureAwait(false);
        return su.Contains("uid=0");
    }
}
