using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Security.Root.Providers;

public sealed class MagiskRootProvider : IRootMethodProvider
{
    private readonly IAdbService _adb;
    private readonly IAdbSyncService _sync;
    private readonly ILogger _logger;

    public MagiskRootProvider(IAdbService adb, IAdbSyncService sync, ILogger? logger = null)
    {
        _adb = adb;
        _sync = sync;
        _logger = logger ?? Log.ForContext<MagiskRootProvider>();
    }

    public RootMethodType MethodType => RootMethodType.Magisk;

    public bool SupportsDevice(DeviceProfile profile) => profile.ApiLevel >= 21;

    public async Task<string> DownloadManagerApkAsync(string targetDir, IProgress<int>? progress, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(targetDir);
        var dest = Path.Combine(targetDir, "Magisk.apk");
        if (File.Exists(dest))
        {
            progress?.Report(100);
            return dest;
        }

        const string url = "https://github.com/topjohnwu/Magisk/releases/latest/download/Magisk.apk";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AndroidManager/1.0");

        _logger.Information("[Root] Magisk APK indiriliyor");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = File.Create(dest);

        var buffer = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            downloaded += read;
            if (total > 0)
                progress?.Report((int)(downloaded * 100 / total));
        }

        return dest;
    }

    public async Task<string> WaitForPatchResultAsync(
        string serial,
        string remoteBootPath,
        string workingDir,
        IProgress<RootStep>? progress,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new RootStep
        {
            Order = 1,
            Title = "Magisk yama bekleniyor",
            Description = "Cihazda yamalı image oluşması izleniyor…",
            Status = RootStepStatus.Running
        });

        // Bağlı timeout — sonsuz polling yok (varsayılan 5 dk).
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeoutCts.Token;

        string? remotePatched = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var findOutput = await _adb.ExecuteShellAsync(
                    "ls /sdcard/Download/magisk_patched*.img /sdcard/Download/*_magisk_patched*.img 2>/dev/null | head -1",
                    token).ConfigureAwait(false);
                remotePatched = findOutput.Trim();
                if (!string.IsNullOrWhiteSpace(remotePatched))
                    break;

                await Task.Delay(2000, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Magisk yaması zaman aşımına uğradı (5 dk). Magisk uygulamasını manuel kontrol edin.");
        }

        if (string.IsNullOrWhiteSpace(remotePatched))
            throw new TimeoutException(
                "Magisk yamalı image zaman aşımı — cihazda Magisk ile patch tamamlayın.");

        var localPath = Path.Combine(workingDir, "magisk_patched.img");
        await _sync.PullAsync(remotePatched, localPath, null, cancellationToken).ConfigureAwait(false);

        progress?.Report(new RootStep
        {
            Order = 2,
            Title = "Yamalı image alındı",
            Description = localPath,
            Status = RootStepStatus.Completed,
            ProgressPercent = 100
        });

        return localPath;
    }

    public async Task<bool> VerifyRootAsync(string serial, CancellationToken cancellationToken = default)
    {
        var output = await _adb.ExecuteShellAsync("su -c id 2>/dev/null", cancellationToken).ConfigureAwait(false);
        return output.Contains("uid=0", StringComparison.Ordinal);
    }
}
