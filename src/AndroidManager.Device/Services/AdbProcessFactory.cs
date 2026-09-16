using System.Diagnostics;
using System.Text;

namespace AndroidManager.Device.Services;

internal static class AdbProcessFactory
{
    public static ProcessStartInfo Create(string adbPath, string arguments, bool redirectInput = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = adbPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (redirectInput)
            psi.StandardInputEncoding = Encoding.UTF8;

        return psi;
    }

    /// <summary>No text encoding — stdout is a raw block stream (dd / exec-out).</summary>
    public static ProcessStartInfo CreateBinary(string adbPath, string arguments)
    {
        return new ProcessStartInfo
        {
            FileName = adbPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    public static Task<(int ExitCode, string Output)> RunOnceAsync(
        string adbPath,
        string arguments,
        CancellationToken cancellationToken) =>
        RunOnceWithTimeoutAsync(
            adbPath,
            arguments,
            AndroidManager.Core.Services.ProcessWaitHelper.DefaultCommandTimeout,
            cancellationToken);

    public static async Task<(int ExitCode, string Output)> RunOnceWithTimeoutAsync(
        string adbPath,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var psi = Create(adbPath, arguments);
        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("adb süreci başlatılamadı.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
            timeoutCts.CancelAfter(timeout);

        await using var killReg = timeoutCts.Token.Register(() =>
            AndroidManager.Core.Services.ProcessWaitHelper.TryKill(process));

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            var output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            if (!string.IsNullOrWhiteSpace(stderr) && !string.IsNullOrWhiteSpace(stdout))
                output = $"{stdout.TrimEnd()}\n{stderr.TrimEnd()}";

            return (process.ExitCode, output.TrimEnd());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AndroidManager.Core.Services.ProcessWaitHelper.TryKill(process);
            return (-1, "adb timeout");
        }
    }
}
