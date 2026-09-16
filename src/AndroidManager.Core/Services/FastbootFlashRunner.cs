using System.Diagnostics;
using System.Text;
using AndroidManager.Core.Models;

namespace AndroidManager.Core.Services;

/// <summary>Platform-tools fastboot.exe süreç yönetimi.</summary>
public sealed class FastbootFlashRunner
{
    public async Task<IReadOnlyList<string>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        var fastboot = PlatformToolsPathResolver.ResolveFastbootPath();
        if (string.IsNullOrWhiteSpace(fastboot))
            return [];

        if (!string.Equals(fastboot, "fastboot", StringComparison.OrdinalIgnoreCase) && !File.Exists(fastboot))
            return [];

        var result = await RunCapturedAsync(
                fastboot,
                "devices",
                serial: null,
                timeout: TimeSpan.FromSeconds(15),
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);

        return ParseDevices(result.Output);
    }

    public async Task<bool> IsFastbootdAsync(string? serial, CancellationToken cancellationToken = default)
    {
        var output = await GetVarAsync(serial, "is-userspace", cancellationToken).ConfigureAwait(false);
        return ParseGetVarValue(output).Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> IsBootloaderFastbootAsync(string? serial, CancellationToken cancellationToken = default)
    {
        var devices = await ListDevicesAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(serial)
            && !devices.Any(d => d.Equals(serial, StringComparison.OrdinalIgnoreCase)))
            return false;

        var output = await GetVarAsync(serial, "is-userspace", cancellationToken).ConfigureAwait(false);
        var value = ParseGetVarValue(output);
        if (value.Equals("yes", StringComparison.OrdinalIgnoreCase))
            return false;
        if (value.Equals("no", StringComparison.OrdinalIgnoreCase))
            return true;

        return devices.Count > 0;
    }

    public Task<string> GetVarAsync(string? serial, string variable, CancellationToken cancellationToken = default) =>
        RunSimpleAsync(serial, $"getvar {variable}", cancellationToken);

    public async Task<FastbootCommandResult> RebootToFastbootdAsync(
        string? serial,
        string rebootCommand,
        IProgress<FlashingProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new FlashingProgressReport
        {
            Stage = CustomRomFlashStage.EnteringFastbootd,
            Percent = 54,
            Message = $"fastbootd: fastboot {rebootCommand}…"
        });

        return await RunAsync(serial, rebootCommand, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FastbootCommandResult> RebootFastbootdAsync(
        string? serial,
        IProgress<FlashingProgressReport>? progress = null,
        CancellationToken cancellationToken = default) =>
        await RebootToFastbootdAsync(serial, "reboot fastboot", progress, cancellationToken).ConfigureAwait(false);

    public async Task<FastbootCommandResult> FlashPartitionAsync(
        string? serial,
        string partition,
        string imagePath,
        bool disableAvbFlags = false,
        IProgress<FlashingProgressReport>? progress = null,
        CancellationToken cancellationToken = default,
        bool globalAvbFlags = false)
    {
        if (!File.Exists(imagePath))
        {
            return new FastbootCommandResult
            {
                Success = false,
                Message = $"İmaj yok: {imagePath}",
                Partition = partition
            };
        }

        var args = disableAvbFlags switch
        {
            true when globalAvbFlags =>
                $"--disable-verity --disable-verification flash {partition} \"{imagePath}\"",
            true => $"flash {partition} \"{imagePath}\" --disable-verity --disable-verification",
            _ => $"flash {partition} \"{imagePath}\""
        };

        progress?.Report(new FlashingProgressReport
        {
            Stage = CustomRomFlashStage.Flashing,
            Percent = 50,
            Message = $"fastboot flash {partition}…",
            Partition = partition
        });

        return await RunAsync(serial, args, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sparse algılamadan ham yazım — EROFS/pad imajlarında "Invalid sparse" önler.</summary>
    public async Task<FastbootCommandResult> FlashPartitionRawAsync(
        string? serial,
        string partition,
        string imagePath,
        IProgress<FlashingProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(imagePath))
        {
            return new FastbootCommandResult
            {
                Success = false,
                Message = $"İmaj yok: {imagePath}",
                Partition = partition
            };
        }

        progress?.Report(new FlashingProgressReport
        {
            Stage = CustomRomFlashStage.Flashing,
            Percent = 50,
            Message = $"fastboot flash:raw {partition}…",
            Partition = partition
        });

        return await RunAsync(
                serial,
                $"flash:raw {partition} \"{imagePath}\"",
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<FastbootCommandResult> ResizeLogicalPartitionAsync(
        string? serial,
        string partition,
        long sizeBytes,
        IProgress<FlashingProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new FlashingProgressReport
        {
            Stage = CustomRomFlashStage.Flashing,
            Percent = 49,
            Message = $"fastboot resize-logical-partition {partition} {sizeBytes}…",
            Partition = partition
        });

        return RunAsync(
            serial,
            $"resize-logical-partition {partition} {sizeBytes}",
            progress,
            cancellationToken);
    }

    public Task<FastbootCommandResult> BootImageAsync(
        string? serial,
        string imagePath,
        IProgress<FlashingProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new FlashingProgressReport
        {
            Stage = CustomRomFlashStage.Flashing,
            Percent = 40,
            Message = $"fastboot boot {Path.GetFileName(imagePath)}…"
        });

        return RunAsync(serial, $"boot \"{imagePath}\"", progress, cancellationToken);
    }

    public Task<FastbootCommandResult> UpdatePackageAsync(
        string? serial,
        string packagePath,
        IProgress<FlashingProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new FlashingProgressReport
        {
            Stage = CustomRomFlashStage.Flashing,
            Percent = 55,
            Message = $"fastboot update {Path.GetFileName(packagePath)}…"
        });

        return RunAsync(serial, $"update \"{packagePath}\"", progress, cancellationToken);
    }

    public Task<FastbootCommandResult> ErasePartitionAsync(
        string? serial,
        string partition,
        IProgress<FlashingProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new FlashingProgressReport
        {
            Stage = CustomRomFlashStage.Flashing,
            Percent = 49,
            Message = $"fastboot erase {partition}…",
            Partition = partition
        });

        return RunAsync(serial, $"erase {partition}", progress, cancellationToken);
    }

    public Task<FastbootCommandResult> RebootBootloaderAsync(
        string? serial,
        IProgress<FlashingProgressReport>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(serial, "reboot bootloader", progress, cancellationToken);

    public Task<FastbootCommandResult> SetActiveSlotAsync(
        string? serial,
        string slot,
        IProgress<FlashingProgressReport>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(serial, $"set_active {slot}", progress, cancellationToken);

    public Task<FastbootCommandResult> RebootAsync(
        string? serial,
        IProgress<FlashingProgressReport>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(serial, "reboot", progress, cancellationToken);

    public async Task<FastbootCommandResult> RunAsync(
        string? serial,
        string arguments,
        IProgress<FlashingProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fastboot = PlatformToolsPathResolver.ResolveFastbootPath();
        if (string.IsNullOrWhiteSpace(fastboot))
        {
            return new FastbootCommandResult
            {
                Success = false,
                Message = "fastboot.exe bulunamadı. Ayarlar → platform-tools yolunu kontrol edin."
            };
        }

        var timeout = ResolveTimeout(arguments);

        // flash komutları: stdout/stderr yönlendirme fastboot'u Windows'ta kilitleyebilir (bat ile aynı mod).
        if (arguments.Contains(" flash ", StringComparison.OrdinalIgnoreCase))
        {
            return await RunDirectAsync(
                    fastboot,
                    arguments,
                    serial,
                    timeout,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await RunCapturedAsync(
                fastboot,
                arguments,
                serial,
                timeout,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> RunSimpleAsync(string? serial, string arguments, CancellationToken cancellationToken)
    {
        var fastboot = PlatformToolsPathResolver.ResolveFastbootPath();
        if (string.IsNullOrWhiteSpace(fastboot))
            return "";

        var result = await RunCapturedAsync(
                fastboot,
                arguments,
                serial,
                TimeSpan.FromSeconds(20),
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);

        return result.Output;
    }

    private static TimeSpan ResolveTimeout(string arguments)
    {
        var a = arguments.ToLowerInvariant();
        if (a.Contains("devices", StringComparison.Ordinal) || a.Contains("getvar", StringComparison.Ordinal))
            return TimeSpan.FromSeconds(20);
        if (a.StartsWith("reboot", StringComparison.Ordinal))
            return TimeSpan.FromSeconds(120);
        if (a.Contains("set_active", StringComparison.Ordinal))
            return TimeSpan.FromSeconds(30);

        if (!a.Contains(" flash ", StringComparison.Ordinal))
            return TimeSpan.FromMinutes(10);

        if (a.Contains(" flash super", StringComparison.Ordinal)
            || a.Contains(" update ", StringComparison.Ordinal))
            return TimeSpan.FromMinutes(90);

        if (a.Contains(" flash system", StringComparison.Ordinal)
            || a.Contains(" flash vendor", StringComparison.Ordinal)
            || a.Contains(" flash product", StringComparison.Ordinal)
            || a.Contains(" flash odm", StringComparison.Ordinal)
            || a.Contains(" flash system_ext", StringComparison.Ordinal))
            return TimeSpan.FromMinutes(45);

        if (a.Contains(" flash modem", StringComparison.Ordinal)
            || a.Contains(" flash xbl", StringComparison.Ordinal))
            return TimeSpan.FromMinutes(15);

        return TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Bat/cmd ile aynı: çıktı yönlendirilmez, yalnızca exit code beklenir.
    /// Windows fastboot redirect altında vbmeta/system flash'ta takılabiliyor.
    /// </summary>
    private static async Task<FastbootCommandResult> RunDirectAsync(
        string fastboot,
        string arguments,
        string? serial,
        TimeSpan timeout,
        IProgress<FlashingProgressReport>? progress,
        CancellationToken cancellationToken) =>
        await DeviceTransportGate.RunAsync(
            ct => RunDirectCoreAsync(fastboot, arguments, serial, timeout, progress, ct),
            cancellationToken).ConfigureAwait(false);

    private static async Task<FastbootCommandResult> RunDirectCoreAsync(
        string fastboot,
        string arguments,
        string? serial,
        TimeSpan timeout,
        IProgress<FlashingProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        var serialArg = string.IsNullOrWhiteSpace(serial) ? "" : $"-s {serial} ";
        var workDir = Path.GetDirectoryName(fastboot);
        var commandLine = $"\"{fastboot}\" {serialArg}{arguments}";

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {commandLine}",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(workDir))
            psi.WorkingDirectory = workDir;

        using var process = Process.Start(psi)
                              ?? throw new InvalidOperationException("fastboot süreci başlatılamadı.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        await using var killReg = timeoutCts.Token.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // best-effort
            }
        });

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = RunFlashHeartbeatAsync(process, arguments, progress, heartbeatCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new FastbootCommandResult
            {
                Success = false,
                Message =
                    $"fastboot zaman aşımı ({FormatTimeout(timeout)}): {arguments}\n\n" +
                    "USB kablosunu çıkar-takın, telefonu fastboot ekranında tutun ve tekrar deneyin. " +
                    "Komut satırında aynı fastboot komutu çalışıyorsa Ayarlar'daki fastboot.exe yolunu kontrol edin."
            };
        }
        finally
        {
            heartbeatCts.Cancel();
            try
            {
                await heartbeat.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        var ok = process.ExitCode == 0;
        progress?.Report(new FlashingProgressReport
        {
            Stage = CustomRomFlashStage.Flashing,
            Percent = 55,
            Message = ok ? "OKAY" : $"fastboot exit {process.ExitCode}",
            DetailLine = ok ? "OKAY" : $"fastboot exit {process.ExitCode}"
        });

        return new FastbootCommandResult
        {
            Success = ok,
            Message = ok ? "OKAY" : $"fastboot exit {process.ExitCode}",
            Output = ok ? "OKAY" : $"exit code {process.ExitCode}"
        };
    }

    private static async Task RunFlashHeartbeatAsync(
        Process process,
        string arguments,
        IProgress<FlashingProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        var elapsed = 0;
        while (!process.HasExited)
        {
            try
            {
                await Task.Delay(2500, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            elapsed += 2500;
            progress?.Report(new FlashingProgressReport
            {
                Stage = CustomRomFlashStage.Flashing,
                Percent = 55,
                Message = $"fastboot yazıyor… ({elapsed / 1000} sn)",
                DetailLine = arguments
            });
        }
    }

    private static async Task<FastbootCommandResult> RunCapturedAsync(
        string fastboot,
        string arguments,
        string? serial,
        TimeSpan timeout,
        IProgress<FlashingProgressReport>? progress,
        CancellationToken cancellationToken) =>
        await DeviceTransportGate.RunAsync(
            ct => RunCapturedCoreAsync(fastboot, arguments, serial, timeout, progress, ct),
            cancellationToken).ConfigureAwait(false);

    private static async Task<FastbootCommandResult> RunCapturedCoreAsync(
        string fastboot,
        string arguments,
        string? serial,
        TimeSpan timeout,
        IProgress<FlashingProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        var psi = BuildProcessStartInfo(fastboot, arguments, serial, captureOutput: true);

        using var process = Process.Start(psi)
                              ?? throw new InvalidOperationException("fastboot süreci başlatılamadı.");

        process.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        await using var killReg = timeoutCts.Token.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // best-effort
            }
        });

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new FastbootCommandResult
            {
                Success = false,
                Message = $"fastboot zaman aşımı ({FormatTimeout(timeout)}): {arguments}"
            };
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var output = string.IsNullOrWhiteSpace(stdout) ? stderr.Trim() : $"{stdout}\n{stderr}".Trim();

        if (!string.IsNullOrWhiteSpace(output))
        {
            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                progress?.Report(new FlashingProgressReport
                {
                    Stage = CustomRomFlashStage.Flashing,
                    Percent = 55,
                    Message = line.Trim(),
                    DetailLine = line.Trim()
                });
            }
        }

        var ok = process.ExitCode == 0
                 && !output.Contains("FAILED", StringComparison.OrdinalIgnoreCase);

        return new FastbootCommandResult
        {
            Success = ok,
            Message = string.IsNullOrWhiteSpace(output)
                ? (ok ? "OKAY" : $"fastboot exit {process.ExitCode}")
                : output,
            Output = output
        };
    }

    private static ProcessStartInfo BuildProcessStartInfo(
        string fastboot,
        string arguments,
        string? serial,
        bool captureOutput)
    {
        var serialArg = string.IsNullOrWhiteSpace(serial) ? "" : $"-s {serial} ";
        var workDir = Path.GetDirectoryName(fastboot);

        var psi = new ProcessStartInfo
        {
            FileName = fastboot,
            Arguments = $"{serialArg}{arguments}",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(workDir))
            psi.WorkingDirectory = workDir;

        if (captureOutput)
        {
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
        }

        return psi;
    }

    public static IReadOnlyList<string> ParseDevices(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return [];

        var list = new List<string>();
        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Trim().Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;

            if (!parts[1].Contains("fastboot", StringComparison.OrdinalIgnoreCase)
                && !parts[1].Contains("bootloader", StringComparison.OrdinalIgnoreCase))
                continue;

            if (parts[0].Length > 0)
                list.Add(parts[0]);
        }

        return list;
    }

    private static string ParseGetVarValue(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return string.Empty;

        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            return line[(colon + 1)..].Trim();
        }

        return output.Trim();
    }

    private static string FormatTimeout(TimeSpan timeout) =>
        timeout.TotalMinutes >= 1
            ? $"{timeout.TotalMinutes:F0} dk"
            : $"{timeout.TotalSeconds:F0} sn";
}

public sealed class FastbootCommandResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string Output { get; init; } = "";
    public string? Partition { get; init; }
}
