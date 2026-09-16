namespace AndroidManager.Security.Services;

internal static class FastbootPartitionHelper
{
    /// <summary>
    /// A/B slot'lu isimleri fastboot'un anladığı kök isme çevirir (boot_a → boot).
    /// </summary>
    public static string ResolveFlashTarget(string partitionName)
    {
        if (string.IsNullOrWhiteSpace(partitionName))
            return partitionName;

        var stem = RomPartitionCatalog.StripSlotSuffix(partitionName);
        return stem switch
        {
            "boot" or "recovery" or "init_boot" or "vendor_boot" => stem,
            _ => partitionName
        };
    }

    public static bool IsBootOrRecoveryStem(string partitionName)
    {
        var stem = RomPartitionCatalog.StripSlotSuffix(partitionName);
        return stem is "boot" or "recovery" or "init_boot" or "vendor_boot";
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1_048_576 => $"{bytes / 1024.0:F0} KB",
        < 1_073_741_824 => $"{bytes / 1_048_576.0:F1} MB",
        _ => $"{bytes / 1_073_741_824.0:F2} GB"
    };

    public static string ExplainFlashFailure(string partitionName, string rawMessage, long fileBytes, long recordedPartitionBytes)
    {
        if (rawMessage.Contains("Volume Full", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("too large", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("more than max allowed", StringComparison.OrdinalIgnoreCase))
        {
            var stem = RomPartitionCatalog.StripSlotSuffix(partitionName);
            var hint = stem is "boot" or "recovery"
                ? " Özel ROM'lar recovery/boot bölümünü küçük tutar; büyük yedek dosyası sığmayabilir. " +
                  "Sadece recovery gerekiyorsa Recovery Manager → «Fastboot Flash Boot» ile daha küçük .img kullanın."
                : " İmaj bölüm kapasitesinden büyük olabilir.";

            return $"{partitionName}: {rawMessage.Trim()}\n" +
                   $"Dosya: {FormatBytes(fileBytes)}" +
                   (recordedPartitionBytes > 0 ? $", yedekte kayıtlı bölüm: {FormatBytes(recordedPartitionBytes)}" : "") +
                   $".{hint}";
        }

        return $"{partitionName}: {rawMessage.Trim()}";
    }
}
