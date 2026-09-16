namespace AndroidManager.Core.Services;

public static class VendorImagePadder
{
    /// <summary>
    /// Patched vendor'ı hedef boyuta (orijinal img veya partition-size) hizalar.
    /// Boyut eşleşince fastbootd resize istemez → "locked devices" azalır.
    /// </summary>
    public static string PrepareForFlash(
        string patchedVendorPath,
        string? originalVendorPath,
        string workDir,
        long? devicePartitionSizeBytes = null)
    {
        if (string.IsNullOrWhiteSpace(patchedVendorPath) || !File.Exists(patchedVendorPath))
            return patchedVendorPath;

        long targetSize = 0;
        if (devicePartitionSizeBytes is > 0)
            targetSize = devicePartitionSizeBytes.Value;
        else if (!string.IsNullOrWhiteSpace(originalVendorPath) && File.Exists(originalVendorPath))
            targetSize = new FileInfo(originalVendorPath).Length;

        var patchedSize = new FileInfo(patchedVendorPath).Length;
        if (targetSize <= 0 || patchedSize == targetSize)
            return patchedVendorPath;

        if (patchedSize > targetSize)
        {
            // Hedef küçük — pad edilemez; orijinali dene (resize gerekebilir).
            return patchedVendorPath;
        }

        Directory.CreateDirectory(workDir);
        var outputPath = Path.Combine(workDir, "patched_vendor_padded.img");

        using (var input = File.OpenRead(patchedVendorPath))
        using (var output = File.Create(outputPath))
        {
            input.CopyTo(output);
            output.SetLength(targetSize);
        }

        return outputPath;
    }
}
