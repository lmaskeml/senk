using System.Diagnostics;

namespace AndroidManager.Core.Services;

/// <summary>Harici süreç (adb/fastboot/scrcpy) için iptal + kill sözleşmesi.</summary>
public static class ProcessWaitHelper
{
    public static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultShellTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DefaultAcquireTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(8);

    public static async Task WaitOrKillAsync(
        Process process,
        TimeSpan budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(budget);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            try
            {
                await process.WaitForExitAsync()
                    .WaitAsync(KillGrace, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // zombie; best-effort
            }
            catch (OperationCanceledException)
            {
                // ignore
            }

            throw;
        }
    }

    public static async Task WaitAfterKillAsync(Process? process)
    {
        if (process is null)
            return;

        TryKill(process);
        try
        {
            await process.WaitForExitAsync()
                .WaitAsync(KillGrace, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // zombie; best-effort
        }
    }

    public static void TryKill(Process? process)
    {
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // best-effort
        }
    }
}
