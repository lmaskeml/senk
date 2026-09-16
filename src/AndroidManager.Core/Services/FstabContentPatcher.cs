using System.Text.RegularExpressions;

namespace AndroidManager.Core.Services;

/// <summary>
/// DFE: eski forceencrypt ve Android 15 FBE (fileencryption / metadata_encryption) bayraklarını kaldırır.
/// charger_fstab dokunulmaz.
/// </summary>
public static class FstabContentPatcher
{
    private static readonly Regex FileEncryptionFlag = new(
        @"(,?)\s*fileencryption=[^\s,]+",
        RegexOptions.CultureInvariant);

    private static readonly Regex MetadataEncryptionFlag = new(
        @"(,?)\s*metadata_encryption=[^\s,]+",
        RegexOptions.CultureInvariant);

    private static readonly Regex KeyDirectoryFlag = new(
        @"(,?)\s*keydirectory=[^\s,]+",
        RegexOptions.CultureInvariant);

    private static readonly Regex ForceFdeOrFbeFlag = new(
        @"(,?)\s*forcefdeorfbe=[^\s,]+",
        RegexOptions.CultureInvariant);

    private static readonly Regex InlineCryptMountFlag = new(
        @",inlinecrypt\b|\binlinecrypt,",
        RegexOptions.CultureInvariant);

    public static readonly string[] KnownFstabFileNames =
    [
        "fstab.qcom",
        "fstab.default",
        "fstab.vili",
        "fstab.qcom.vili",
        "fstab",
    ];

    public static readonly string[] KnownFstabRelativePaths =
    [
        "etc/fstab.qcom",
        "etc/fstab.default",
        "etc/fstab.vili",
        "etc/fstab.qcom.vili",
        "etc/fstab",
    ];

    public static bool IsMainFstabFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;
        if (fileName.StartsWith("charger_", StringComparison.OrdinalIgnoreCase))
            return false;

        return fileName.StartsWith("fstab", StringComparison.OrdinalIgnoreCase);
    }

    public static string DevicePathForFileName(string fileName) => $"/vendor/etc/{fileName}";

    public static bool NeedsPatch(string content)
    {
        if (string.IsNullOrEmpty(content))
            return false;

        return content.Contains("forceencrypt", StringComparison.Ordinal)
               || content.Contains("fileencryption=", StringComparison.Ordinal)
               || content.Contains("metadata_encryption=", StringComparison.Ordinal)
               || content.Contains("keydirectory=", StringComparison.Ordinal)
               || content.Contains("forcefdeorfbe=", StringComparison.Ordinal)
               || HasStandaloneInlineCrypt(content);
    }

    public static string PatchContent(string content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        var patched = content;
        if (patched.Contains("forceencrypt", StringComparison.Ordinal))
            patched = patched.Replace("forceencrypt", "encryptable", StringComparison.Ordinal);

        patched = FileEncryptionFlag.Replace(patched, "");
        patched = MetadataEncryptionFlag.Replace(patched, "");
        patched = KeyDirectoryFlag.Replace(patched, "");
        patched = ForceFdeOrFbeFlag.Replace(patched, "");
        patched = InlineCryptMountFlag.Replace(patched, "");
        return CleanCommaRuns(patched);
    }

    private static bool HasStandaloneInlineCrypt(string content)
    {
        foreach (var line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.TrimStart().StartsWith('#'))
                continue;
            if (line.Contains("fileencryption=", StringComparison.Ordinal))
                continue;
            if (Regex.IsMatch(line, @"\binlinecrypt\b"))
                return true;
        }

        return false;
    }

    private static string CleanCommaRuns(string content)
    {
        var cleaned = Regex.Replace(content, ",{2,}", ",");
        cleaned = Regex.Replace(cleaned, @",(\s)", "$1");
        return cleaned;
    }
}
