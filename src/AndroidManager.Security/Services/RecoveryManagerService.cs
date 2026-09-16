using AndroidManager.Core;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Security.Services;

public sealed class RecoveryManagerService : IRecoveryManagerService
{
    private readonly IAdbService _adb;
    private readonly IDeviceToolsService _tools;
    private readonly IRootAnalysisService _analysis;
    private readonly IElevatedShellService _su;
    private readonly IFastbootDiscoveryService _fastboot;
    private readonly ILogger _logger;

    public RecoveryManagerService(
        IAdbService adb,
        IDeviceToolsService tools,
        IRootAnalysisService analysis,
        IElevatedShellService su,
        IFastbootDiscoveryService fastboot,
        ILogger? logger = null)
    {
        _adb = adb;
        _tools = tools;
        _analysis = analysis;
        _su = su;
        _fastboot = fastboot;
        _logger = logger ?? Log.ForContext<RecoveryManagerService>();
    }

    public async Task<RecoveryStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_adb.SelectedDevice is null)
            await _adb.WaitForAdbDeviceAsync(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);

        var device = _adb.SelectedDevice;
        if (device is null)
        {
            var fastbootDevices = await _fastboot.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
            if (fastbootDevices.Count > 0)
            {
                var fb = fastbootDevices[0];
                return new RecoveryStatus
                {
                    Label = "Fastboot / Bootloader",
                    Detail = $"Serial: {fb.Serial}. ADB gerekmez; payload ROM ve fastboot flash için uygun.",
                    ConnectionMode = DeviceConnectionMode.Fastboot
                };
            }

            return new RecoveryStatus
            {
                Label = "Cihaz bağlı değil",
                Detail = "ADB veya fastboot bağlantısı yok. Brick kurtarma için telefonu fastboot modunda USB ile takın.",
                ConnectionMode = DeviceConnectionMode.Offline
            };
        }

        string props = "";
        DeviceProfile? profile = null;
        try
        {
            props = await _adb.ExecuteShellAsync(
                    "getprop ro.bootmode; getprop sys.usb.state; getprop ro.twrp.version; getprop ro.orangefox.version",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[Recovery] getprop failed (sideload olabilir)");
        }

        var mode = RecoveryConnectionDetector.FromDevice(device, props);
        if (mode == DeviceConnectionMode.Adb && device.IsSideload)
            mode = DeviceConnectionMode.Recovery;

        try
        {
            profile = await _analysis.AnalyzeDeviceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[Recovery] Profile in recovery failed");
        }

        var type = profile?.DetectedRecovery ?? RecoveryType.Unknown;
        if (type is RecoveryType.Unknown or RecoveryType.Stock)
        {
            if (RecoveryConnectionDetector.LooksLikeCustomRecovery(props) || device.IsRecovery || device.IsSideload)
                type = RecoveryConnectionDetector.LooksLikeCustomRecovery(props) && props.Contains("orangefox", StringComparison.OrdinalIgnoreCase)
                    ? RecoveryType.OrangeFox
                    : RecoveryType.Twrp;
        }

        return new RecoveryStatus
        {
            Type = type,
            Label = type switch
            {
                RecoveryType.Twrp => "TWRP",
                RecoveryType.OrangeFox => "OrangeFox",
                RecoveryType.Stock => "Stok Recovery",
                RecoveryType.Custom => "Özel Recovery",
                _ => device.IsRecovery ? "Recovery (ADB)" : "Bilinmiyor"
            },
            BootloaderUnlocked = profile?.BootloaderUnlocked ?? false,
            IsRooted = profile?.IsRooted ?? false,
            ConnectionMode = mode,
            Detail = $"ADB: {device.State} · {device.Serial}"
        };
    }

    public Task<DeviceToolResult> RebootRecoveryAsync(CancellationToken cancellationToken = default) =>
        _tools.RebootAsync(DeviceRebootMode.Recovery, cancellationToken);

    public async Task<DeviceToolResult> FlashRecoveryImageAsync(
        string imagePath,
        RecoveryFlashMethod method,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(imagePath))
            return new DeviceToolResult { Success = false, Message = $"İmaj dosyası bulunamadı: {imagePath}" };

        var (targetSerial, resolveError) = await ResolveFastbootTargetAsync(cancellationToken).ConfigureAwait(false);
        if (resolveError is not null)
            return resolveError;

        var plan = await RecoveryImageFlashPlanner
            .PlanAsync(targetSerial, imagePath, method, cancellationToken)
            .ConfigureAwait(false);

        _logger.Information("[Recovery] TWRP flash planı: {Summary}", plan.Summary);

        DeviceToolResult? lastFailure = null;
        foreach (var attempt in plan.Attempts)
        {
            _logger.Information("[Recovery] Deneme: {Desc}", attempt.Description);
            var result = await ExecuteRecoveryFlashAttemptAsync(
                    targetSerial, imagePath, attempt.Method, cancellationToken)
                .ConfigureAwait(false);

            if (result.Success)
            {
                // Kalıcı istenmişken planner'ın «fastboot boot (geçici)» düşüşünü başarı sayma
                if (method is RecoveryFlashMethod.Fastboot or RecoveryFlashMethod.FastbootFlashBoot
                    && attempt.Method == RecoveryFlashMethod.FastbootBootOnce)
                {
                    lastFailure = new DeviceToolResult
                    {
                        Success = false,
                        Message = plan.Summary + Environment.NewLine +
                                  "Kalıcı yazım sığmadı — yalnızca geçici fastboot boot mümkün.\n" +
                                  result.Message
                    };
                    break;
                }

                return new DeviceToolResult
                {
                    Success = true,
                    Message = plan.Summary + Environment.NewLine + attempt.Description + Environment.NewLine + result.Message
                };
            }

            lastFailure = result;
            if (!IsRetriableFlashError(result.Message))
                break;
        }

        var imageSize = new FileInfo(imagePath).Length;
        var hint = FastbootPartitionHelper.ExplainFlashFailure(
            "boot/recovery", lastFailure?.Message ?? "bilinmeyen hata", imageSize, 0);

        return new DeviceToolResult
        {
            Success = false,
            Message = plan.Summary + Environment.NewLine + Environment.NewLine + hint +
                      Environment.NewLine +
                      "Öneri: «Geçici — fastboot boot» seçin (192 MB TWRP boot'a sığmaz; sideload için yeterli). " +
                      "Kalıcı yazım için recovery flash veya daha küçük TWRP deneyin."
        };
    }

    private async Task<DeviceToolResult> ExecuteRecoveryFlashAttemptAsync(
        string targetSerial,
        string imagePath,
        RecoveryFlashMethod method,
        CancellationToken cancellationToken)
    {
        if (method == RecoveryFlashMethod.Fastboot)
            return await FlashViaFastbootAsync(imagePath, cancellationToken).ConfigureAwait(false);

        if (method == RecoveryFlashMethod.FastbootFlashBoot)
            return await FlashViaFastbootBootAsync(imagePath, cancellationToken).ConfigureAwait(false);

        if (method == RecoveryFlashMethod.FastbootBootOnce)
            return await BootViaFastbootAsync(imagePath, cancellationToken).ConfigureAwait(false);

        return await FlashViaRootDdAsync(imagePath, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsRetriableFlashError(string message) =>
        message.Contains("more than max allowed", StringComparison.OrdinalIgnoreCase)
        || message.Contains("too large", StringComparison.OrdinalIgnoreCase)
        || message.Contains("too big", StringComparison.OrdinalIgnoreCase)
        || message.Contains("partition size: 0", StringComparison.OrdinalIgnoreCase)
        || message.Contains("Volume Full", StringComparison.OrdinalIgnoreCase);

    public async Task<DeviceToolResult> InstallRecoveryZipsAsync(
        IReadOnlyList<string> zipPaths,
        RecoveryZipInstallMethod method,
        CancellationToken cancellationToken = default)
    {
        if (zipPaths is null || zipPaths.Count == 0)
            return new DeviceToolResult { Success = false, Message = "Kurulacak zip dosyası bulunamadı." };

        if (method == RecoveryZipInstallMethod.TWRPAdbInstall)
            return await InstallViaTwrpAdbAsync(zipPaths, cancellationToken).ConfigureAwait(false);

        return await InstallViaRootOtaUpdatePackageAsync(zipPaths, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DeviceToolResult> InstallViaTwrpAdbAsync(
        IReadOnlyList<string> zipPaths,
        CancellationToken ct)
    {
        var status = await GetStatusAsync(ct).ConfigureAwait(false);
        if (status.ConnectionMode != DeviceConnectionMode.Recovery)
            return new DeviceToolResult
            {
                Success = false,
                Message = "Cihaz recovery modunda görünmüyor (TWRP/OrangeFox ADB ile kurulum için recovery gerekir)."
            };

        if (status.Type is RecoveryType.Stock or RecoveryType.Unknown)
            return new DeviceToolResult
            {
                Success = false,
                Message = "Bu recovery tipi TWRP/OrangeFox komutlarıyla zip kurulum için uygun değil."
            };

        // Keep zips in RAM/tmp for faster & less destructive recovery installs.
        const string remoteDir = "/tmp/androidmanager_zip";
        await _adbExecuteAsync($"mkdir -p \"{remoteDir}\"", ct).ConfigureAwait(false);

        for (var i = 0; i < zipPaths.Count; i++)
        {
            var zipPath = zipPaths[i];
            if (!File.Exists(zipPath))
                return new DeviceToolResult { Success = false, Message = $"Zip bulunamadı: {zipPath}" };

            var baseName = Path.GetFileName(zipPath);
            baseName = string.IsNullOrWhiteSpace(baseName) ? $"zip_{i}.zip" : baseName;
            var remoteZip = $"{remoteDir}/{i}_{baseName}";

            _logger.Information("[Recovery] Pushing install zip {Zip} -> {Remote}", zipPath, remoteZip);
            var pushed = await RunAdbPushAsync(zipPath, remoteZip, ct).ConfigureAwait(false);
            if (!pushed)
                return new DeviceToolResult { Success = false, Message = "Zip cihaza gönderilemedi (adb push başarısız)." };

            var installCmd = BuildRecoveryInstallCommand(status.Type, remoteZip);
            var exitMarker = "__ANDROIMANAGER_EXIT__:";
            // Capture the command exit code reliably.
            var cmd = $"{installCmd}; echo \"{exitMarker}$?\"";
            var output = await _adb.ExecuteShellAsync(cmd, ct).ConfigureAwait(false);

            var exitCode = TryExtractExitCode(output, exitMarker);
            if (exitCode is null || exitCode != 0)
            {
                var tail = TruncateForMessage(output, 900);
                return new DeviceToolResult
                {
                    Success = false,
                    Message = $"Zip kurulumu başarısız. Komut: {installCmd}\nÇıktı:\n{tail}"
                };
            }

            // Best-effort cleanup; install may keep temp files so errors here are ignored.
            await _adbExecuteAsync($"rm -f \"{remoteZip}\" 2>/dev/null || true", ct).ConfigureAwait(false);
        }

        return new DeviceToolResult { Success = true, Message = "Recovery zip kurulumları tamamlandı." };
    }

    private async Task<DeviceToolResult> InstallViaRootOtaUpdatePackageAsync(
        IReadOnlyList<string> zipPaths,
        CancellationToken ct)
    {
        if (zipPaths.Count != 1)
            return new DeviceToolResult
            {
                Success = false,
                Message = "Root canlı (OTA-command) yöntemi tek seferde tek zip yüklemeyi destekler. Lütfen sadece 1 dosya seçin."
            };

        if (!await _su.IsAvailableAsync(ct).ConfigureAwait(false))
            return new DeviceToolResult { Success = false, Message = "Root erişimi yok. Bu yöntem için cihazda Magisk/KernelSU/APatch root gerekir." };

        if (!await WaitForAdbAsync(ct).ConfigureAwait(false))
            return new DeviceToolResult { Success = false, Message = "ADB bağlantısı yok. Zip'i recovery'ye kopyalamak için ADB gerekir." };

        var zipPath = zipPaths[0];
        if (!File.Exists(zipPath))
            return new DeviceToolResult { Success = false, Message = $"Zip bulunamadı: {zipPath}" };

        var baseName = Path.GetFileName(zipPath);
        baseName = string.IsNullOrWhiteSpace(baseName) ? "update.zip" : baseName;
        var remoteDir = "/data/local/tmp/androidmanager_recovery";
        var remoteZip = $"{remoteDir}/{baseName}";

        await _adb.ExecuteShellAsync($"mkdir -p \"{remoteDir}\"", ct).ConfigureAwait(false);
        _logger.Information("[Recovery] Root OTA: pushing {Zip} -> {Remote}", zipPath, remoteZip);
        var pushed = await RunAdbPushAsync(zipPath, remoteZip, ct).ConfigureAwait(false);
        if (!pushed)
            return new DeviceToolResult { Success = false, Message = "Zip cihaza gönderilemedi (adb push başarısız)." };

        var cmdWrittenMarker = "__ANDROIMANAGER_WRITTEN__:";
        var writtenTo = await _su.RunAsync(
            $"for p in /cache/recovery/command /metadata/recovery/command /data/recovery/command; do " +
            "mkdir -p \"$(dirname \"$p\")\" 2>/dev/null; " +
            $"echo \"--update_package={remoteZip}\" > \"$p\" 2>/dev/null && " +
            $"echo \"{cmdWrittenMarker}$p\" && break; " +
            "done",
            timeout: TimeSpan.FromSeconds(20),
            cancellationToken: ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(writtenTo) || !writtenTo.Contains(cmdWrittenMarker, StringComparison.OrdinalIgnoreCase))
            return new DeviceToolResult
            {
                Success = false,
                Message = "Recovery command dosyası yazılamadı. /cache veya /metadata alanı cihazınızda farklı olabilir."
            };

        await _su.RunAsync("reboot recovery", timeout: TimeSpan.FromSeconds(10), cancellationToken: ct).ConfigureAwait(false);
        return new DeviceToolResult
        {
            Success = true,
            Message = "Root ile recovery'e yeniden başlatıldı. Cihaz yeniden açılınca zip kurulumu recovery içinde yapılacaktır."
        };
    }

    private async Task<bool> WaitForAdbAsync(CancellationToken ct)
    {
        if (_adb.SelectedDevice is not null)
            return true;

        await _adb.WaitForAdbDeviceAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        return _adb.SelectedDevice is not null;
    }

    private static string BuildRecoveryInstallCommand(RecoveryType recoveryType, string remoteZip) =>
        recoveryType switch
        {
            RecoveryType.Twrp => $"twrp install \"{remoteZip}\"",
            RecoveryType.OrangeFox => $"fox install \"{remoteZip}\"",
            _ => $"twrp install \"{remoteZip}\""
        };

    private static int? TryExtractExitCode(string output, string exitMarker)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        var idx = output.IndexOf(exitMarker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        var after = output[(idx + exitMarker.Length)..].Trim();
        // Expect a line like: __MARKER__:0
        var firstToken = after.Split(['\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(firstToken, out var code) ? code : null;
    }

    private static string TruncateForMessage(string text, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        if (text.Length <= maxLen)
            return text;

        return text[..maxLen] + "...";
    }

    private Task<string> _adbExecuteAsync(string command, CancellationToken ct) =>
        _adb.ExecuteShellAsync(command, ct);

    private async Task<(string Serial, DeviceToolResult? Error)> ResolveFastbootTargetAsync(CancellationToken ct)
    {
        var adbSerial = _adb.SelectedDevice?.Serial ?? "";
        var devices = await FastbootDeviceProbe.GetFastbootDevicesAsync(ct).ConfigureAwait(false);
        if (devices.Count == 0)
        {
            _logger.Information("[Recovery] Fastboot cihaz yok, bootloader'a reboot ediliyor…");
            await _tools.RebootAsync(DeviceRebootMode.Bootloader, ct).ConfigureAwait(false);
            await Task.Delay(8000, ct).ConfigureAwait(false);
            devices = await FastbootDeviceProbe.GetFastbootDevicesAsync(ct).ConfigureAwait(false);
            if (devices.Count == 0)
            {
                var fastbootExe = PlatformToolsPathResolver.ResolveFastbootPath();
                return ("", new DeviceToolResult
                {
                    Success = false,
                    Message =
                        "Fastboot modunda cihaz bulunamadı.\n\n" +
                        $"Kullanılan fastboot: {fastbootExe}\n\n" +
                        "Komut satırında «fastboot devices» çalışıyorsa Ayarlar → " +
                        "fastboot.exe yolunu aynı klasöre ayarlayın (ör. Minimal ADB and Fastboot).\n\n" +
                        "Cihaz zaten fastboot ekranındaysa USB kablosunu çıkar-takıp tekrar deneyin."
                });
            }
        }

        var targetSerial = devices.FirstOrDefault(d => d.Equals(adbSerial, StringComparison.OrdinalIgnoreCase))
                           ?? devices[0];
        return (targetSerial, null);
    }

    private async Task<DeviceToolResult> BootViaFastbootAsync(string imagePath, CancellationToken ct)
    {
        _logger.Information("[Recovery] Fastboot boot (once): {Path}", imagePath);

        var (targetSerial, error) = await ResolveFastbootTargetAsync(ct).ConfigureAwait(false);
        if (error is not null)
            return error;

        return await FastbootDeviceProbe.BootImageAsync(targetSerial, imagePath, ct).ConfigureAwait(false);
    }

    private async Task<DeviceToolResult> FlashViaFastbootBootAsync(string imagePath, CancellationToken ct)
    {
        _logger.Information("[Recovery] Fastboot flash boot: {Path}", imagePath);

        var (targetSerial, error) = await ResolveFastbootTargetAsync(ct).ConfigureAwait(false);
        if (error is not null)
            return error;

        // fastboot flash boot → A/B cihazlarda aktif slota boot_a / boot_b yazar (Xiaomi vb.)
        var result = await FastbootDeviceProbe.FlashPartitionAsync(targetSerial, "boot", imagePath, ct)
            .ConfigureAwait(false);

        return result.Success
            ? new DeviceToolResult
            {
                Success = true,
                Message = result.Message + Environment.NewLine +
                          "Recovery boot bölümüne yazıldı. Cihazı recovery'ye almak için güç + ses tuşları veya «Recovery'ye Yeniden Başlat» kullanın."
            }
            : result;
    }

    private async Task<DeviceToolResult> FlashViaFastbootAsync(string imagePath, CancellationToken ct)
    {
        _logger.Information("[Recovery] Fastboot flash recovery: {Path}", imagePath);

        var (targetSerial, error) = await ResolveFastbootTargetAsync(ct).ConfigureAwait(false);
        if (error is not null)
            return error;

        var recoveryPartition = await ResolveFastbootRecoveryPartitionAsync(targetSerial, ct).ConfigureAwait(false);
        var firstAttempt = await FastbootDeviceProbe.FlashPartitionAsync(targetSerial, recoveryPartition, imagePath, ct)
            .ConfigureAwait(false);

        // Bazı A/B cihazlarda recovery_a/recovery_b isimleri yoktur; sadece "recovery" vardır.
        // Slot bazlı deneme başarısız olursa mesaj içeriğine güvenmeden bir kez daha "recovery" deneriz.
        if (!firstAttempt.Success &&
            !string.Equals(recoveryPartition, "recovery", StringComparison.OrdinalIgnoreCase))
        {
            var secondAttempt = await FastbootDeviceProbe.FlashPartitionAsync(targetSerial, "recovery", imagePath, ct)
                .ConfigureAwait(false);
            if (secondAttempt.Success)
                return secondAttempt;

            firstAttempt = secondAttempt;
        }

        if (!firstAttempt.Success && IsImageLargerThanPartitionError(firstAttempt.Message))
            return AppendBootOnceHint(firstAttempt);

        return firstAttempt;
    }

    private static bool IsImageLargerThanPartitionError(string message) =>
        message.Contains("more than max allowed", StringComparison.OrdinalIgnoreCase)
        || message.Contains("image too large", StringComparison.OrdinalIgnoreCase)
        || message.Contains("too big", StringComparison.OrdinalIgnoreCase);

    private static DeviceToolResult AppendBootOnceHint(DeviceToolResult failed) =>
        new()
        {
            Success = false,
            Message = failed.Message + Environment.NewLine + Environment.NewLine +
                      "İmaj recovery bölümünden büyük olabilir. Xiaomi / özel ROM'larda " +
                      "«Fastboot Flash Boot» (fastboot flash boot) yöntemini deneyin; " +
                      "geçici deneme için «Fastboot Boot (Geçici)» de kullanılabilir."
        };

    private async Task<DeviceToolResult> FlashViaRootDdAsync(string imagePath, CancellationToken ct)
    {
        if (!await _su.IsAvailableAsync(ct).ConfigureAwait(false))
            return new DeviceToolResult { Success = false, Message = "Root erişimi yok. Bu yöntem için cihazda root gereklidir." };

        _logger.Information("[Recovery] Root dd flash: {Path}", imagePath);

        var slotSuffix = (await _su.RunAsync("getprop ro.boot.slot_suffix 2>/dev/null", cancellationToken: ct).ConfigureAwait(false))
            .Trim();

        if (string.IsNullOrWhiteSpace(slotSuffix))
            slotSuffix = "";

        var partPath = await _su.RunAsync(
                $"ls -1 /dev/block/by-name/recovery{slotSuffix} 2>/dev/null | head -1",
                cancellationToken: ct)
            .ConfigureAwait(false);
        partPath = partPath.Trim();

        if (string.IsNullOrWhiteSpace(partPath))
        {
            partPath = await _su.RunAsync(
                    $"ls -1 /dev/block/by-name/RECOVERY{slotSuffix} 2>/dev/null | head -1",
                    cancellationToken: ct)
                .ConfigureAwait(false);
            partPath = partPath.Trim();
        }

        if (string.IsNullOrWhiteSpace(partPath))
        {
            partPath = await _su.RunAsync(
                    "find /dev/block/by-name -name recovery -o -name RECOVERY 2>/dev/null | head -1",
                    cancellationToken: ct)
                .ConfigureAwait(false);
            partPath = partPath.Trim();
        }

        if (string.IsNullOrWhiteSpace(partPath))
        {
            partPath = await _su.RunAsync("ls -la /dev/block/platform/*/by-name/recovery 2>/dev/null | awk '{print $NF}' | head -1", cancellationToken: ct)
                .ConfigureAwait(false);
            partPath = partPath.Trim();
        }

        if (string.IsNullOrWhiteSpace(partPath))
            return new DeviceToolResult { Success = false, Message = "Recovery bölüm yolu tespit edilemedi. Cihaz A/B bölümleme şeması kullanıyor olabilir." };

        const string remoteTmp = "/data/local/tmp/_recovery_flash.img";

        await _adb.ExecuteShellAsync($"rm -f {remoteTmp}", ct).ConfigureAwait(false);

        var fileInfo = new FileInfo(imagePath);
        _logger.Debug("[Recovery] Pushing image ({Size} bytes)…", fileInfo.Length);

        var adbPushResult = await RunAdbPushAsync(imagePath, remoteTmp, ct).ConfigureAwait(false);
        if (!adbPushResult)
            return new DeviceToolResult { Success = false, Message = "İmaj dosyası cihaza gönderilemedi." };

        var ddResult = await _su.RunAsync($"dd if={remoteTmp} of={partPath} bs=4096 && sync", cancellationToken: ct)
            .ConfigureAwait(false);

        await _su.RunAsync($"rm -f {remoteTmp}", cancellationToken: ct).ConfigureAwait(false);

        var success = !ddResult.Contains("error", StringComparison.OrdinalIgnoreCase)
                      && !ddResult.Contains("denied", StringComparison.OrdinalIgnoreCase);

        return new DeviceToolResult
        {
            Success = success,
            Message = success
                ? $"Recovery imajı başarıyla yazıldı → {partPath}"
                : $"dd hatası: {ddResult}"
        };
    }

    private static async Task<string> ResolveFastbootRecoveryPartitionAsync(string serial, CancellationToken ct)
    {
        var fastboot = PlatformToolsPathResolver.ResolveFastbootPath();
        var serialArg = string.IsNullOrWhiteSpace(serial) ? "" : $"-s \"{serial}\" ";

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fastboot,
            Arguments = $"{serialArg}getvar current-slot",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        try
        {
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
                return "recovery";

            await using var reg = ct.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            });

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var output = string.IsNullOrWhiteSpace(stdout) ? stderr : $"{stdout}\n{stderr}";

            string? slot = null;
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Contains("current-slot", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmed.Split(':');
                    if (parts.Length >= 2)
                        slot = parts[1].Trim();
                }
            }

            slot ??= output.Contains("current-slot", StringComparison.OrdinalIgnoreCase)
                ? output.Contains("a", StringComparison.OrdinalIgnoreCase) ? "a" : output.Contains("b", StringComparison.OrdinalIgnoreCase) ? "b" : null
                : null;

            if (slot is null)
                return "recovery";

            slot = slot.Trim().ToLowerInvariant();
            return slot is "a" or "b" ? $"recovery_{slot}" : "recovery";
        }
        catch
        {
            return "recovery";
        }
    }

    private async Task<bool> RunAdbPushAsync(string localPath, string remotePath, CancellationToken ct)
    {
        var serial = _adb.SelectedDevice?.Serial;
        var (exitCode, _) = await _adb.RunHostAdbAsync(
            serial,
            $"push \"{localPath}\" \"{remotePath}\"",
            TimeSpan.FromMinutes(2),
            ct).ConfigureAwait(false);
        return exitCode == 0;
    }
}
