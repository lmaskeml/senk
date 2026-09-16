using System.Diagnostics;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Transfer.Services;

/// <summary>
/// Detects iOS devices and runs idevicebackup2 via bundled libimobiledevice tools.
/// </summary>
public sealed class IosDeviceService : IIosDeviceService
{
    private readonly ILogger _logger;

    public IosDeviceService(ILogger? logger = null) =>
        _logger = logger ?? Log.ForContext<IosDeviceService>();

    public bool ToolsAvailable => ResolveTool("idevice_id.exe") is not null;

    public string ToolsSearchHint
    {
        get
        {
            var baseDir = AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return $"Beklenen konum: {Path.Combine(baseDir, "ios")}";
        }
    }

    public bool IsAppleMobileDeviceServiceAvailable =>
        Directory.Exists(@"C:\Program Files\Common Files\Apple\Mobile Device Support")
        || Directory.Exists(@"C:\Program Files (x86)\Common Files\Apple\Mobile Device Support");

    public async Task<IReadOnlyList<IosDeviceInfo>> GetConnectedDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var tool = ResolveTool("idevice_id.exe");
        if (tool is null)
            throw new FileNotFoundException(
                "idevice_id.exe bulunamadı. " + ToolsSearchHint,
                "idevice_id.exe");

        try
        {
            var (exitCode, stdout, stderr) = await RunToolCaptureDetailedAsync(
                    tool, "-l", TimeSpan.FromSeconds(20), cancellationToken)
                .ConfigureAwait(false);

            var devices = new List<IosDeviceInfo>();
            foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var udid = line.Trim();
                if (udid.Length < 8 || udid.Contains(' ', StringComparison.Ordinal))
                    continue;
                if (udid.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                    continue;
                devices.Add(new IosDeviceInfo { Udid = udid, DeviceName = udid[..Math.Min(8, udid.Length)] + "…" });
            }

            if (devices.Count == 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                _logger.Warning("idevice_id found no devices (exit={Exit}): {Detail}", exitCode, detail.Trim());

                if (!IsAppleMobileDeviceServiceAvailable)
                {
                    throw new InvalidOperationException(
                        "Apple Mobile Device Support kurulu değil.\n\n" +
                        "PowerShell (yönetici):\n" +
                        "  winget install Apple.AppleMobileDeviceSupport\n\n" +
                        "Ardından iPhone'u çıkarıp tekrar takın, kilidi açın ve 'Güven' deyin.");
                }

                if (detail.Contains("Unable to retrieve", StringComparison.OrdinalIgnoreCase) ||
                    exitCode != 0)
                {
                    throw new InvalidOperationException(
                        "iPhone listelenemedi. Kontrol edin:\n" +
                        "• iPhone USB ile bağlı ve açık\n" +
                        "• Telefonda 'Bu bilgisayara güven' onaylı\n" +
                        "• Apple Mobile Device Service çalışıyor (services.msc)\n" +
                        "• Kabloyu çıkarıp yeniden takın (sürücü kurulumundan sonra şart)\n\n" +
                        (string.IsNullOrWhiteSpace(detail) ? "" : detail.Trim()));
                }

                // exit 0 ama boş liste — genelde güven/yeniden takma gerekir
                throw new InvalidOperationException(
                    "iPhone USB'de görünebilir ama henüz eşleşmedi.\n\n" +
                    "1) iPhone kablosunu çıkarın\n" +
                    "2) Tekrar takın (doğrudan anakart USB, hub değil)\n" +
                    "3) Kilidi açın → 'Bu bilgisayara güven?' → Güven\n" +
                    "4) Bu ekranda 'iPhone'ları Yenile'ye basın");
            }

            foreach (var device in devices)
            {
                try
                {
                    var infoTool = ResolveTool("ideviceinfo.exe");
                    if (infoTool is null)
                        continue;
                    var (code, infoOut, _) = await RunToolCaptureDetailedAsync(
                            infoTool,
                            $"-u {device.Udid} -k DeviceName",
                            TimeSpan.FromSeconds(10),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (code == 0)
                    {
                        var name = infoOut.Trim();
                        if (!string.IsNullOrWhiteSpace(name))
                            device.DeviceName = name;
                    }
                }
                catch
                {
                    // keep short udid label
                }
            }

            return devices;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "idevice_id failed");
            throw new InvalidOperationException(
                "idevice_id çalıştırılamadı. ios klasöründeki DLL'lerin eksik olmadığından emin olun.\n" +
                ex.Message,
                ex);
        }
    }

    public async Task<string> CreateUnencryptedBackupAsync(
        string udid,
        string backupDirectory,
        IProgress<WhatsAppTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(backupDirectory);
        var tool = RequireTool("idevicebackup2.exe");

        Report(progress, WhatsAppTransferPhase.PreparingIosBackup, 10,
            "iPhone yedeği alınıyor — cihazda 'Güven' ve yedeklemeyi onaylayın…");

        var args = $"backup --full \"{backupDirectory}\" -u {udid}";
        await RunToolAsync(tool, args, TimeSpan.FromHours(2), progress, cancellationToken)
            .ConfigureAwait(false);

        if (!File.Exists(Path.Combine(backupDirectory, "Manifest.db")))
        {
            var nested = Directory.GetDirectories(backupDirectory).FirstOrDefault(d =>
                File.Exists(Path.Combine(d, "Manifest.db")));
            if (nested is not null)
                return nested;

            throw new InvalidOperationException(
                "Yedek oluşturulamadı. iPhone'da ekran kilidini açık tutun ve 'Bu bilgisayara güven' deyin.");
        }

        return backupDirectory;
    }

    public async Task RestoreBackupAsync(
        string udid,
        string backupDirectory,
        IProgress<WhatsAppTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var tool = RequireTool("idevicebackup2.exe");
        Report(progress, WhatsAppTransferPhase.RestoringIos, 10,
            "WhatsApp verisi iPhone'a geri yükleniyor — cihazı bağlı tutun…");

        var args = $"restore --system --settings --remove \"{backupDirectory}\" -u {udid}";
        await RunToolAsync(tool, args, TimeSpan.FromHours(2), progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RunToolAsync(
        string toolPath,
        string arguments,
        TimeSpan timeout,
        IProgress<WhatsAppTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var workDir = Path.GetDirectoryName(toolPath) ?? AppContext.BaseDirectory;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = toolPath,
                Arguments = arguments,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Report(progress, WhatsAppTransferPhase.PreparingIosBackup, 50, e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Report(progress, WhatsAppTransferPhase.PreparingIosBackup, 50, e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new TimeoutException($"iOS aracı zaman aşımına uğradı: {Path.GetFileName(toolPath)}");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"iOS aracı hata verdi ({Path.GetFileName(toolPath)}): çıkış kodu {process.ExitCode}");
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunToolCaptureDetailedAsync(
        string toolPath,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var workDir = Path.GetDirectoryName(toolPath) ?? AppContext.BaseDirectory;
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = toolPath,
            Arguments = arguments,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("iOS aracı başlatılamadı.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return (process.ExitCode, stdout, stderr);
    }

    private string RequireTool(string fileName)
    {
        var path = ResolveTool(fileName);
        if (path is null)
            throw new FileNotFoundException(
                $"{fileName} bulunamadı. {ToolsSearchHint}",
                fileName);
        return path;
    }

    private static string? ResolveTool(string fileName)
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "ios", fileName),
            Path.Combine(baseDir, "tools", "ios", fileName),
            Path.Combine(baseDir, fileName),
        };

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch
            {
                // ignore
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(dir.Trim(), fileName);
            if (File.Exists(full))
                return full;
        }

        return null;
    }

    private static void Report(
        IProgress<WhatsAppTransferProgress>? progress,
        WhatsAppTransferPhase phase,
        int percent,
        string message) =>
        progress?.Report(new WhatsAppTransferProgress { Phase = phase, Percent = percent, Message = message });
}
