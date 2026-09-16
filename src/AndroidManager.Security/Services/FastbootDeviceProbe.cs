using System.Diagnostics;
using System.Globalization;
using System.Text;
using AndroidManager.Core;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Services;
using Serilog;

namespace AndroidManager.Security.Services;

/// <summary>
/// Fastboot device enumeration for Rescue Center / bootloop assist.
/// </summary>
internal static class FastbootDeviceProbe
{
    public static async Task<IReadOnlyList<string>> GetFastbootDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var fastboot = PlatformToolsPathResolver.ResolveFastbootPath();
        Log.Debug("[Fastboot] devices via {Path}", fastboot);
        if (string.IsNullOrWhiteSpace(fastboot))
            return [];

        if (!string.Equals(fastboot, "fastboot", StringComparison.OrdinalIgnoreCase)
            && !File.Exists(fastboot))
        {
            Log.Debug("[Fastboot] fastboot.exe bulunamadı: {Path}", fastboot);
            return [];
        }

        var psi = new ProcessStartInfo
        {
            FileName = fastboot,
            Arguments = "devices",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        try
        {
            return await DeviceTransportGate.RunAsync(async outerCt =>
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
                timeoutCts.CancelAfter(ProcessWaitHelper.DefaultProbeTimeout);
                var ct = timeoutCts.Token;

                using var process = Process.Start(psi);
                if (process is null)
                    return Array.Empty<string>();

                await using var killReg = ct.Register(() => ProcessWaitHelper.TryKill(process));
                var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
                var stderrTask = process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);
                var output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;

                if (string.IsNullOrWhiteSpace(output))
                    Log.Warning("[Fastboot] devices boş çıktı ({Path})", fastboot);
                else
                    Log.Debug("[Fastboot] devices çıktısı: {Output}", output.Trim());

                return ParseDevices(output);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Fastboot] devices sorgusu başarısız");
            return [];
        }
    }

    public static async Task<bool> IsDeviceInFastbootAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serial))
            return false;

        var devices = await GetFastbootDevicesAsync(cancellationToken).ConfigureAwait(false);
        return devices.Any(d =>
            d.Equals(serial, StringComparison.OrdinalIgnoreCase)
            || serial.Contains(d, StringComparison.OrdinalIgnoreCase)
            || d.Contains(serial, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<long?> GetPartitionSizeBytesAsync(
        string serial,
        string partition,
        CancellationToken cancellationToken = default)
    {
        var raw = await GetVarRawAsync(serial, $"partition-size:{partition}", cancellationToken)
            .ConfigureAwait(false);
        return ParseHexOrDecimalSize(raw);
    }

    public static async Task<long?> GetVarNumericBytesAsync(
        string serial,
        string variable,
        CancellationToken cancellationToken = default)
    {
        var raw = await GetVarRawAsync(serial, variable, cancellationToken).ConfigureAwait(false);
        return ParseHexOrDecimalSize(raw);
    }

    public static async Task<DeviceToolResult> RebootAsync(
        string serial,
        string target,
        CancellationToken cancellationToken = default)
    {
        var fastboot = PlatformToolsPathResolver.ResolveFastbootPath();
        if (string.IsNullOrWhiteSpace(fastboot))
            return new DeviceToolResult { Success = false, Message = "fastboot.exe bulunamadı." };

        var serialArg = string.IsNullOrWhiteSpace(serial) ? "" : $"-s \"{serial}\" ";
        return await RunFastbootAsync(
            fastboot,
            $"{serialArg}reboot {target}",
            okMessage: $"fastboot reboot {target} gönderildi.",
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<DeviceToolResult> BootImageAsync(
        string serial,
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(imagePath))
            return new DeviceToolResult { Success = false, Message = $"İmaj yok: {imagePath}" };

        var fastboot = PlatformToolsPathResolver.ResolveFastbootPath();
        if (string.IsNullOrWhiteSpace(fastboot))
            return new DeviceToolResult { Success = false, Message = "fastboot.exe bulunamadı." };

        var serialArg = string.IsNullOrWhiteSpace(serial) ? "" : $"-s \"{serial}\" ";
        Log.Information("[Fastboot] boot (geçici) {Path} → {Serial}", imagePath, serial);
        return await RunFastbootAsync(
            fastboot,
            $"{serialArg}boot \"{imagePath}\"",
            okMessage: "Geçici TWRP başlatıldı (fastboot boot — kalıcı yazılmadı).",
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<DeviceToolResult> FlashPartitionAsync(
        string serial,
        string partition,
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(partition))
            return new DeviceToolResult { Success = false, Message = "Bölüm adı boş." };
        if (!File.Exists(imagePath))
            return new DeviceToolResult { Success = false, Message = $"İmaj yok: {imagePath}" };

        var fastboot = PlatformToolsPathResolver.ResolveFastbootPath();
        if (string.IsNullOrWhiteSpace(fastboot))
            return new DeviceToolResult { Success = false, Message = "fastboot.exe bulunamadı." };

        Log.Information("[Fastboot] flash {Partition} via {Path}", partition, fastboot);

        var flashTarget = FastbootPartitionHelper.ResolveFlashTarget(partition);
        if (!string.Equals(flashTarget, partition, StringComparison.OrdinalIgnoreCase))
            Log.Information("[Fastboot] {Partition} → {Target}", partition, flashTarget);

        var serialArg = string.IsNullOrWhiteSpace(serial) ? "" : $"-s \"{serial}\" ";
        return await RunFastbootAsync(
            fastboot,
            $"{serialArg}flash {flashTarget} \"{imagePath}\"",
            okMessage: "Flash tamam.",
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<DeviceToolResult> ErasePartitionAsync(
        string serial,
        string partition,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(partition))
            return new DeviceToolResult { Success = false, Message = "Bölüm adı boş." };

        var fastboot = PlatformToolsPathResolver.ResolveFastbootPath();
        if (string.IsNullOrWhiteSpace(fastboot))
            return new DeviceToolResult { Success = false, Message = "fastboot.exe bulunamadı." };

        Log.Information("[Fastboot] erase {Partition} via {Path}", partition, fastboot);

        // erase için slot kök isme çevirme (boot→boot); userdata/metadata olduğu gibi kalır.
        var eraseTarget = partition;
        var serialArg = string.IsNullOrWhiteSpace(serial) ? "" : $"-s \"{serial}\" ";
        return await RunFastbootAsync(
            fastboot,
            $"{serialArg}erase {eraseTarget}",
            okMessage: $"erase {eraseTarget} tamam.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>fastboot -w — userdata + cache (Xiaomi'de partition-size 0 olsa da çalışır).</summary>
    public static Task<DeviceToolResult> WipeUserDataAndCacheAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        var fastboot = PlatformToolsPathResolver.ResolveFastbootPath();
        if (string.IsNullOrWhiteSpace(fastboot))
            return Task.FromResult(new DeviceToolResult { Success = false, Message = "fastboot.exe bulunamadı." });

        var serialArg = string.IsNullOrWhiteSpace(serial) ? "" : $"-s \"{serial}\" ";
        return RunFastbootAsync(
            fastboot,
            $"{serialArg}-w",
            okMessage: "fastboot -w tamam.",
            cancellationToken);
    }

    public static bool LooksLikeFastbootOk(string? output) =>
        !string.IsNullOrWhiteSpace(output)
        && (output.Contains("OKAY", StringComparison.OrdinalIgnoreCase)
            || output.Contains("erase successfully", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Finished. Total time", StringComparison.OrdinalIgnoreCase));

    public static bool LooksLikeUnknownPartition(string? output) =>
        !string.IsNullOrWhiteSpace(output)
        && (output.Contains("unknown partition", StringComparison.OrdinalIgnoreCase)
            || output.Contains("cannot get partition", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Invalid partition", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Partition table doesn't exist", StringComparison.OrdinalIgnoreCase));

    private static async Task<string> GetVarRawAsync(
        string serial,
        string variable,
        CancellationToken cancellationToken)
    {
        var fastboot = PlatformToolsPathResolver.ResolveFastbootPath();
        if (string.IsNullOrWhiteSpace(fastboot))
            return "";

        var serialArg = string.IsNullOrWhiteSpace(serial) ? "" : $"-s \"{serial}\" ";
        var result = await RunFastbootAsync(
            fastboot,
            $"{serialArg}getvar {variable}",
            okMessage: "",
            cancellationToken).ConfigureAwait(false);

        return result.Message;
    }

    private static long? ParseHexOrDecimalSize(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            var value = line[(colon + 1)..].Trim();
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
                return hex;

            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec))
                return dec;
        }

        return null;
    }

    private static async Task<DeviceToolResult> RunFastbootAsync(
        string fastboot,
        string arguments,
        string okMessage,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fastboot,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        try
        {
            return await DeviceTransportGate.RunAsync(async outerCt =>
            {
                var budget = arguments.Contains("flash", StringComparison.OrdinalIgnoreCase)
                             || arguments.Contains("boot ", StringComparison.OrdinalIgnoreCase)
                    ? TimeSpan.FromMinutes(10)
                    : ProcessWaitHelper.DefaultProbeTimeout;

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
                timeoutCts.CancelAfter(budget);
                var ct = timeoutCts.Token;

                using var process = Process.Start(psi);
                if (process is null)
                    return new DeviceToolResult { Success = false, Message = "fastboot süreci başlatılamadı." };

                await using var killReg = ct.Register(() => ProcessWaitHelper.TryKill(process));

                var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
                var stderrTask = process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);
                var output = string.IsNullOrWhiteSpace(stdout) ? stderr : $"{stdout}\n{stderr}";

                var ok = process.ExitCode == 0
                         && !output.Contains("FAILED", StringComparison.OrdinalIgnoreCase);

                return new DeviceToolResult
                {
                    Success = ok,
                    Message = string.IsNullOrWhiteSpace(output)
                        ? (ok ? okMessage : $"fastboot exit {process.ExitCode}")
                        : output.Trim()
                };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Fastboot] {Args} failed", arguments);
            return new DeviceToolResult { Success = false, Message = ex.Message };
        }
    }

    internal static IReadOnlyList<string> ParseDevices(string output) =>
        AndroidManager.Core.Services.FastbootFlashRunner.ParseDevices(output);
}
