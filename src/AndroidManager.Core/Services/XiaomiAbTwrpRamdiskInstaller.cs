using System.Text;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;

namespace AndroidManager.Core.Services;

/// <summary>
/// Xiaomi A/B boot-as-recovery (vili vb.): ham «fastboot flash boot twrp.img» sistemi bozar.
/// Doğru kalıcı yöntem: ROM boot'u koru + TWRP recovery ramdisk enjekte et (Install Recovery Ramdisk).
/// </summary>
internal static class XiaomiAbTwrpRamdiskInstaller
{
    public static async Task<(bool Success, string Message)> TryInstallAsync(
        FastbootFlashRunner fastboot,
        IAdbService adb,
        string fastbootSerial,
        string romBootImagePath,
        string twrpImagePath,
        string activeSlot,
        IProgress<FlashingProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(romBootImagePath) || !File.Exists(twrpImagePath))
            return (false, "ROM boot veya TWRP imajı bulunamadı.");

        // 1) Sistem açılışı için ROM boot (önceki yanlış «flash boot twrp» düzeltmesi)
        Report(progress, 97, "ROM boot geri yazılıyor (sistem açılışı için)…");
        var bootFlash = await fastboot
            .FlashPartitionAsync(
                fastbootSerial,
                "boot",
                romBootImagePath,
                disableAvbFlags: false,
                progress,
                cancellationToken)
            .ConfigureAwait(false);

        if (!bootFlash.Success)
        {
            var slotTarget = activeSlot is "a" or "b" ? $"boot_{activeSlot}" : "boot_a";
            bootFlash = await fastboot
                .FlashPartitionAsync(
                    fastbootSerial,
                    slotTarget,
                    romBootImagePath,
                    disableAvbFlags: false,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!bootFlash.Success)
            return (false, "ROM boot yazılamadı — sistem açılamayabilir:\n" + bootFlash.Message);

        // 2) Geçici TWRP (RAM) — diskteki boot ROM kalır
        Report(progress, 97, "TWRP geçici açılıyor (fastboot boot) — Recovery Ramdisk…");
        var bootTwrp = await fastboot
            .BootImageAsync(fastbootSerial, twrpImagePath, progress, cancellationToken)
            .ConfigureAwait(false);
        if (!bootTwrp.Success)
            return (false,
                "ROM boot yazıldı (sistem açılmalı) ama geçici TWRP açılamadı.\n" +
                "Kalıcı TWRP: fastboot boot twrp.img → Advanced → Install Recovery Ramdisk.\n" +
                bootTwrp.Message);

        var adbDevice = await adb.WaitForAdbDeviceAsync(TimeSpan.FromSeconds(90), cancellationToken)
            .ConfigureAwait(false);
        var adbSerial = adbDevice?.Serial;
        if (string.IsNullOrWhiteSpace(adbSerial))
            return (false,
                "ROM boot yazıldı. TWRP ADB yok — kalıcı ramdisk atlandı.\n" +
                "Telefonda Advanced → Install Recovery Ramdisk ile TWRP .img seçin.");

        var remoteTw = "/tmp/am_twrp_ramdisk.img";
        Report(progress, 98, "TWRP imajı gönderiliyor (ramdisk enjeksiyonu)…");
        var push = await RunAdbAsync(adb, adbSerial, $"push \"{twrpImagePath}\" {remoteTw}", TimeSpan.FromMinutes(5), cancellationToken)
            .ConfigureAwait(false);
        if (push.ExitCode != 0)
            return (false,
                "ROM boot OK. adb push TWRP başarısız — manuel: Advanced → Install Recovery Ramdisk.\n" +
                push.Output);

        var slotSuffix = activeSlot is "a" or "b" ? $"_{activeSlot}" : "_a";
        var script = BuildMagiskbootScript(remoteTw, slotSuffix);
        var remoteScript = "/tmp/am_install_twrp_ramdisk.sh";
        var localScript = Path.Combine(Path.GetTempPath(), "am_install_twrp_ramdisk.sh");
        await File.WriteAllTextAsync(localScript, script.Replace("\r\n", "\n"), new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);

        var pushScript = await RunAdbAsync(
                adb,
                adbSerial,
                $"push \"{localScript}\" {remoteScript}",
                TimeSpan.FromSeconds(60),
                cancellationToken)
            .ConfigureAwait(false);
        if (pushScript.ExitCode != 0)
            return (false,
                "ROM boot OK. Ramdisk script push başarısız — manuel Install Recovery Ramdisk kullanın.\n" +
                pushScript.Output);

        Report(progress, 98, "Recovery Ramdisk yazılıyor (magiskboot)…");
        var run = await RunAdbShellAsync(
                adb,
                adbSerial,
                $"chmod 755 {remoteScript}; sh {remoteScript}",
                cancellationToken)
            .ConfigureAwait(false);

        _ = await RunAdbShellAsync(adb, adbSerial, $"rm -f {remoteTw} {remoteScript}", cancellationToken)
            .ConfigureAwait(false);

        if (!run.Output.Contains("RAMDISK_OK", StringComparison.Ordinal))
        {
            return (false,
                "ROM boot yazıldı — sistem açılmalı.\n" +
                "Kalıcı TWRP otomatik ramdisk başarısız. TWRP'de:\n" +
                "Advanced → Install Recovery Ramdisk → twrp.img\n\n" +
                TrimOut(run.Output));
        }

        Report(progress, 99, "✅ Recovery Ramdisk yazıldı — sisteme geçiliyor…");
        _ = await adb.RebootTransportAsync(null, adbSerial, cancellationToken).ConfigureAwait(false);
        return (true, "ROM boot + TWRP Recovery Ramdisk yazıldı; sistem yeniden başlatılıyor.");
    }

    private static string BuildMagiskbootScript(string remoteTwImg, string slotSuffix) =>
        $$"""
          #!/sbin/sh
          set -e
          TW="{{remoteTwImg}}"
          SLOT="{{slotSuffix}}"
          BOOT=""
          for p in \
            /dev/block/by-name/boot$SLOT \
            /dev/block/bootdevice/by-name/boot$SLOT \
            /dev/block/mapper/boot$SLOT \
            /dev/block/by-name/boot \
            /dev/block/bootdevice/by-name/boot
          do
            [ -e "$p" ] && BOOT="$p" && break
          done
          if [ -z "$BOOT" ]; then
            BOOT=$(find /dev/block -name "boot$SLOT" 2>/dev/null | head -1)
          fi
          if [ -z "$BOOT" ] || [ ! -e "$TW" ]; then
            echo RAMDISK_FAIL_NO_BOOT
            exit 1
          fi
          MB=$(command -v magiskboot || true)
          [ -z "$MB" ] && MB=$(command -v /system/bin/magiskboot || true)
          [ -z "$MB" ] && MB=$(command -v /sbin/magiskboot || true)
          [ -z "$MB" ] && MB=$(ls /system/bin/magiskboot /sbin/magiskboot /system/xbin/magiskboot 2>/dev/null | head -1)
          if [ -z "$MB" ] || [ ! -x "$MB" ]; then
            echo RAMDISK_FAIL_NO_MAGISKBOOT
            exit 1
          fi
          WORK=/tmp/am_twrp_rd
          rm -rf "$WORK"
          mkdir -p "$WORK/boot" "$WORK/twrp"
          dd if="$BOOT" of="$WORK/boot.img" bs=1M
          cd "$WORK/boot"
          "$MB" unpack ../boot.img
          cd "$WORK/twrp"
          "$MB" unpack "$TW"
          if [ ! -f ramdisk.cpio ]; then
            echo RAMDISK_FAIL_NO_TWRP_RAMDISK
            exit 1
          fi
          cp -f ramdisk.cpio "$WORK/boot/ramdisk.cpio"
          cd "$WORK/boot"
          "$MB" repack ../boot.img ../new_boot.img
          if [ ! -f "$WORK/new_boot.img" ]; then
            echo RAMDISK_FAIL_REPACK
            exit 1
          fi
          dd if="$WORK/new_boot.img" of="$BOOT" bs=1M
          sync
          echo RAMDISK_OK
          echo TARGET=$BOOT
          """;

    private static string TrimOut(string output)
    {
        var t = output.Trim();
        return t.Length <= 800 ? t : t[^800..];
    }

    private static void Report(IProgress<FlashingProgressReport>? progress, int percent, string message) =>
        progress?.Report(new FlashingProgressReport
        {
            Stage = CustomRomFlashStage.Finalizing,
            Percent = percent,
            Message = message
        });

    private static Task<(int ExitCode, string Output)> RunAdbAsync(
        IAdbService adb,
        string serial,
        string argsAfterSerial,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        adb.RunHostAdbAsync(serial, argsAfterSerial, timeout, cancellationToken);

    private static Task<(int ExitCode, string Output)> RunAdbShellAsync(
        IAdbService adb,
        string serial,
        string shellCommand,
        CancellationToken cancellationToken)
    {
        var quoted = shellCommand.Replace("\"", "\\\"", StringComparison.Ordinal);
        return RunAdbAsync(adb, serial, $"shell \"{quoted}\"", TimeSpan.FromMinutes(3), cancellationToken);
    }
}
