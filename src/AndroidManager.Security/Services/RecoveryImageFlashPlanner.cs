using System.Globalization;
using AndroidManager.Core;
using AndroidManager.Core.Abstractions;

namespace AndroidManager.Security.Services;

internal sealed record RecoveryFlashAttempt(
    RecoveryFlashMethod Method,
    string Description);

internal sealed record RecoveryFlashPlan(
    IReadOnlyList<RecoveryFlashAttempt> Attempts,
    string Summary);

/// <summary>TWRP imaj boyutu vs fastboot partition-size — boot/recovery/boot-once seçimi.</summary>
internal static class RecoveryImageFlashPlanner
{
    /// <summary>vili boot_a/boot_b fastboot'ta ~196608 KB (201326592 bayt).</summary>
    private const long TypicalBootCapBytes = 196608L * 1024;

    public static async Task<RecoveryFlashPlan> PlanAsync(
        string serial,
        string imagePath,
        RecoveryFlashMethod requestedMethod,
        CancellationToken cancellationToken = default)
    {
        var imageSize = new FileInfo(imagePath).Length;
        var bootSize = await ReadBestPartitionSizeAsync(serial, "boot", cancellationToken).ConfigureAwait(false);
        var recoverySize = await ReadBestPartitionSizeAsync(serial, "recovery", cancellationToken).ConfigureAwait(false);
        var maxDownload = await ReadMaxDownloadSizeAsync(serial, cancellationToken).ConfigureAwait(false);

        var attempts = new List<RecoveryFlashAttempt>();
        void Add(RecoveryFlashMethod method, string desc)
        {
            if (attempts.Any(a => a.Method == method))
                return;
            attempts.Add(new RecoveryFlashAttempt(method, desc));
        }

        bool Fits(long? partSize) => partSize is > 0 && imageSize <= partSize;
        bool CanBootOnce() => maxDownload is > 0 && imageSize <= maxDownload;

        switch (requestedMethod)
        {
            case RecoveryFlashMethod.FastbootFlashBoot when Fits(bootSize):
                Add(RecoveryFlashMethod.FastbootFlashBoot, "fastboot flash boot");
                break;
            case RecoveryFlashMethod.FastbootFlashBoot:
                if (Fits(recoverySize))
                    Add(RecoveryFlashMethod.Fastboot, "boot sığmıyor — fastboot flash recovery deneniyor");
                if (CanBootOnce())
                    Add(RecoveryFlashMethod.FastbootBootOnce, "kalıcı yazma sığmıyor — fastboot boot (geçici TWRP, sideload için yeterli)");
                if (attempts.Count == 0)
                    Add(RecoveryFlashMethod.FastbootFlashBoot, "boot (muhtemelen başarısız — bölüm küçük)");
                break;

            case RecoveryFlashMethod.Fastboot when Fits(recoverySize):
                Add(RecoveryFlashMethod.Fastboot, "fastboot flash recovery");
                break;
            case RecoveryFlashMethod.Fastboot:
                if (Fits(bootSize))
                    Add(RecoveryFlashMethod.FastbootFlashBoot, "recovery sığmıyor — fastboot flash boot");
                if (CanBootOnce())
                    Add(RecoveryFlashMethod.FastbootBootOnce, "fastboot boot (geçici TWRP)");
                if (attempts.Count == 0)
                    Add(RecoveryFlashMethod.Fastboot, "recovery (muhtemelen başarısız)");
                break;

            case RecoveryFlashMethod.FastbootBootOnce when CanBootOnce():
                Add(RecoveryFlashMethod.FastbootBootOnce, "fastboot boot (geçici)");
                break;
            default:
                Add(requestedMethod, requestedMethod.ToString());
                break;
        }

        if (attempts.Count == 0)
        {
            if (Fits(recoverySize))
                Add(RecoveryFlashMethod.Fastboot, "fastboot flash recovery");
            else if (Fits(bootSize))
                Add(RecoveryFlashMethod.FastbootFlashBoot, "fastboot flash boot");
            else if (CanBootOnce())
                Add(RecoveryFlashMethod.FastbootBootOnce, "fastboot boot (geçici TWRP)");
        }

        var summary =
            $"TWRP imaj: {FastbootPartitionHelper.FormatBytes(imageSize)} · " +
            $"boot: {FormatSize(bootSize)} · recovery: {FormatSize(recoverySize)} · " +
            $"max-download: {FormatSize(maxDownload)}";

        if (imageSize > TypicalBootCapBytes && requestedMethod == RecoveryFlashMethod.FastbootFlashBoot)
        {
            summary += " · A15 TWRP genelde boot'a sığmaz; recovery veya geçici boot kullanın.";
        }

        return new RecoveryFlashPlan(attempts, summary);
    }

    public static int SuggestWizardFlashMethodIndex(long imageBytes, string? codename)
    {
        // Kalıcı flash için: boot veya recovery — geçici boot ayrı launch modunda.
        if (imageBytes > 196608L * 1024)
            return 0;

        return 1;
    }

    private static string FormatSize(long? bytes) =>
        bytes is > 0 ? FastbootPartitionHelper.FormatBytes(bytes.Value) : "?";

    private static async Task<long?> ReadBestPartitionSizeAsync(
        string serial,
        string stem,
        CancellationToken cancellationToken)
    {
        foreach (var name in new[] { stem, $"{stem}_a", $"{stem}_b" })
        {
            var size = await FastbootDeviceProbe.GetPartitionSizeBytesAsync(serial, name, cancellationToken)
                .ConfigureAwait(false);
            if (size is > 0)
                return size;
        }

        return null;
    }

    private static async Task<long?> ReadMaxDownloadSizeAsync(
        string serial,
        CancellationToken cancellationToken) =>
        await FastbootDeviceProbe.GetVarNumericBytesAsync(serial, "max-download-size", cancellationToken)
            .ConfigureAwait(false);
}
