using System.Buffers.Binary;
using System.Text;

namespace AndroidManager.Core.Services;

/// <summary>
/// AVB vbmeta yardımcıları.
/// Birincil: stock flash_all.bat — <c>flash vbmeta_ab img --disable-verity --disable-verification</c>.
/// Xiaomi Minimal ADB AVB_MAGIC verirse flags image içine yazılır (bayraksız flash).
/// </summary>
internal static class VbmetaImageHelper
{
    // libavb: HASHTREE_DISABLED = 1<<0, VERIFICATION_DISABLED = 1<<1
    private const uint FlagHashtreeDisabled = 1;
    private const uint FlagVerificationDisabled = 2;
    private const int FlagsFieldOffset = 120;

    public const int PreferredStubSizeBytes = 4096;

    public static byte[] CreateDisabledImage(int sizeBytes) => CreateDisabledStubImage(sizeBytes);

    public static byte[] CreateDisabledStubImage(int sizeBytes)
    {
        if (sizeBytes < 256)
            sizeBytes = 256;

        var image = new byte[sizeBytes];
        Encoding.ASCII.GetBytes("AVB0").CopyTo(image, 0);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(4), 1); // major
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(8), 0); // minor
        BinaryPrimitives.WriteUInt32BigEndian(
            image.AsSpan(FlagsFieldOffset),
            FlagHashtreeDisabled | FlagVerificationDisabled);
        return image;
    }

    public static int DefaultSizeForPartition(string partitionName)
    {
        _ = partitionName;
        return PreferredStubSizeBytes;
    }

    public static async Task<string> WriteDisabledStubAsync(
        string workDir,
        string partitionName,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workDir);
        var bytes = CreateDisabledStubImage(DefaultSizeForPartition(partitionName));
        var path = Path.Combine(workDir, $"_{partitionName}_avb_disabled.img");
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);

        if (partitionName.Equals("vbmeta", StringComparison.OrdinalIgnoreCase))
        {
            var alias = Path.Combine(workDir, "_vbmeta_4k.img");
            await File.WriteAllBytesAsync(alias, bytes, cancellationToken).ConfigureAwait(false);
        }

        return path;
    }

    /// <summary>
    /// ROM vbmeta kopyası — flags=VERIFICATION|HASHTREE disabled.
    /// Bayraksız flash edilir (Windows fastboot --disable-verity AVB_MAGIC hatasını önler).
    /// </summary>
    public static async Task<string?> WriteDisabledRomCopyAsync(
        string romVbmetaPath,
        string workDir,
        string partitionName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(romVbmetaPath) || !File.Exists(romVbmetaPath))
            return null;

        var source = await File.ReadAllBytesAsync(romVbmetaPath, cancellationToken).ConfigureAwait(false);
        if (source.Length < FlagsFieldOffset + 4)
            return null;

        if (source[0] != (byte)'A' || source[1] != (byte)'V' || source[2] != (byte)'B' || source[3] != (byte)'0')
            return null;

        BinaryPrimitives.WriteUInt32BigEndian(
            source.AsSpan(FlagsFieldOffset),
            FlagHashtreeDisabled | FlagVerificationDisabled);

        Directory.CreateDirectory(workDir);
        var path = Path.Combine(workDir, $"_{partitionName}_rom_disabled.img");
        await File.WriteAllBytesAsync(path, source, cancellationToken).ConfigureAwait(false);
        return path;
    }

    /// <summary>
    /// 1) Stock flash_all.bat: ham ROM + CLI flags (image'den sonra).
    /// 2) ROM kopyası flags=disabled, bayraksız — Xiaomi Minimal ADB AVB_MAGIC yedeği.
    /// 3) Global --disable-verity flash …
    /// 4) Stub son çare.
    /// </summary>
    public static IEnumerable<(string ImagePath, bool DisableFlags, bool GlobalFlags)> EnumerateFlashAttempts(
        string? romDisabledCopyPath,
        string? romPath,
        string stubPath,
        bool romHasAvb)
    {
        var hasRom = !string.IsNullOrWhiteSpace(romPath)
                     && File.Exists(romPath)
                     && new FileInfo(romPath!).Length > 256;

        if (hasRom)
            yield return (romPath!, true, false);

        if (!string.IsNullOrWhiteSpace(romDisabledCopyPath)
            && File.Exists(romDisabledCopyPath)
            && new FileInfo(romDisabledCopyPath).Length > 256)
        {
            yield return (romDisabledCopyPath, false, false);
        }

        if (hasRom)
        {
            yield return (romPath!, true, true);
            if (romHasAvb)
                yield return (romPath!, false, false);
        }

        yield return (stubPath, true, false);
        yield return (stubPath, false, false);
        yield return (stubPath, true, true);
    }
}
