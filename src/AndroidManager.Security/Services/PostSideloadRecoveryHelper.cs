using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Security.Services;

/// <summary>Sideload bitince TWRP shell'e dönüş — sideload modunda shell/bootloader reboot çalışmaz.</summary>
internal static class PostSideloadRecoveryHelper
{
    public static async Task<DeviceToolResult> EnsureRecoveryShellAsync(
        IAdbService adb,
        ILogger logger,
        IProgress<CustomRomInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Report(progress, 87, "Sideload tamamlandı — kurulum moduna dönülüyor…");

        if (await WaitForRecoveryShellInternalAsync(adb, logger, TimeSpan.FromSeconds(25), cancellationToken)
                .ConfigureAwait(false))
        {
            return Ok("Kurulum modu hazır (ADB shell).");
        }

        var serial = await ResolveSerialAsync(adb, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(serial))
        {
            return Fail(
                "Sideload sonrası cihaz görünmüyor.\n\n" +
                "TWRP'de sideload ekranındaysanız geri tuşu ile ana menüye dönün veya «Reboot → Recovery» seçin.");
        }

        Report(progress, 88, "Sideload modundan çıkılıyor (adb reboot recovery)…");
        var rebootRecovery = await adb.RebootTransportAsync("recovery", serial, cancellationToken)
            .ConfigureAwait(false);
        if (!rebootRecovery.Success)
            logger.Warning("[CustomROM] adb reboot recovery: {Msg}", rebootRecovery.Message);

        await Task.Delay(8000, cancellationToken).ConfigureAwait(false);

        if (await WaitForRecoveryShellInternalAsync(adb, logger, TimeSpan.FromSeconds(90), cancellationToken)
                .ConfigureAwait(false))
        {
            return Ok("Kurulum modu hazır.");
        }

        return Fail(
            "TWRP ADB shell açılamadı (sideload modunda takılı kalmış olabilir).\n\n" +
            "TWRP ana menüsüne dönün → Reboot → Recovery, ardından manager'da tekrar deneyin.\n\n" +
            "Manuel boot flash: Reboot → Bootloader → fastboot flash boot");
    }

    public static async Task<DeviceToolResult> RebootToBootloaderAsync(
        IAdbService adb,
        IFastbootDiscoveryService fastboot,
        ILogger logger,
        IProgress<CustomRomInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Report(progress, 96, "Fastboot'a geçiliyor…");

        var existingFb = await fastboot.GetFirstSerialAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(existingFb))
            return Ok(existingFb);

        var serial = await ResolveSerialAsync(adb, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(serial))
        {
            var reboot = await adb.RebootTransportAsync("bootloader", serial, cancellationToken)
                .ConfigureAwait(false);
            if (!reboot.Success)
                logger.Warning("[CustomROM] adb reboot bootloader: {Msg}", reboot.Message);
        }
        else
        {
            logger.Warning("[CustomROM] Bootloader reboot — ADB serial yok, fastboot bekleniyor");
        }

        await Task.Delay(8000, cancellationToken).ConfigureAwait(false);

        var fbSerial = await WaitForFastbootSerialAsync(fastboot, TimeSpan.FromSeconds(180), cancellationToken)
            .ConfigureAwait(false);
        if (fbSerial is not null)
            return Ok(fbSerial);

        // Son deneme
        if (!string.IsNullOrWhiteSpace(serial))
        {
            await adb.RebootTransportAsync("bootloader", serial, cancellationToken).ConfigureAwait(false);
            await Task.Delay(8000, cancellationToken).ConfigureAwait(false);
            fbSerial = await WaitForFastbootSerialAsync(fastboot, TimeSpan.FromSeconds(60), cancellationToken)
                .ConfigureAwait(false);
            if (fbSerial is not null)
                return Ok(fbSerial);
        }

        return Fail(
            "Boot yazımı için fastboot cihazı bulunamadı.\n\n" +
            "TWRP sideload ekranındaysanız: ana menü → Reboot → Bootloader.\n\n" +
            "Sonra manager'da «Yeni ROM» ile yalnızca boot tamamlama adımını tekrarlayın veya manuel:\n" +
            "fastboot flash boot boot.img");
    }

    private static async Task<bool> WaitForRecoveryShellInternalAsync(
        IAdbService adb,
        ILogger logger,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var devices = await adb.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
            var recovery = devices.FirstOrDefault(d => d.IsRecovery && !d.IsSideload)
                           ?? devices.FirstOrDefault(d => d.IsRecovery);

            if (recovery is not null)
            {
                adb.SelectDevice(recovery);
                if (await ShellRespondsAsync(adb, cancellationToken).ConfigureAwait(false))
                {
                    logger.Information("[CustomROM] Recovery shell ready: {Serial} ({State})", recovery.Serial, recovery.State);
                    return true;
                }
            }

            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<bool> ShellRespondsAsync(IAdbService adb, CancellationToken cancellationToken)
    {
        try
        {
            var output = await adb.ExecuteShellAsync("echo AM_RECOVERY_OK", cancellationToken).ConfigureAwait(false);
            return output.Contains("AM_RECOVERY_OK", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> ResolveSerialAsync(IAdbService adb, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(adb.SelectedDevice?.Serial))
            return adb.SelectedDevice.Serial;

        var devices = await adb.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        return devices.FirstOrDefault(d => d.IsAdbReady)?.Serial;
    }

    private static async Task<string?> WaitForFastbootSerialAsync(
        IFastbootDiscoveryService fastboot,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var serial = await fastboot.GetFirstSerialAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(serial))
                return serial;

            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static DeviceToolResult Ok(string message) => new() { Success = true, Message = message };

    private static DeviceToolResult Fail(string message) => new() { Success = false, Message = message };

    private static void Report(
        IProgress<CustomRomInstallProgress>? progress,
        int percent,
        string message) =>
        progress?.Report(new CustomRomInstallProgress
        {
            Stage = CustomRomInstallStage.PatchingFstab,
            Percent = Math.Clamp(percent, 0, 100),
            Message = message
        });
}
