using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using AndroidManager.Core.Models;

namespace AndroidManager.Security.Services;

public static partial class CustomRomPackageInspector
{
    private const int MaxScriptBytes = 2 * 1024 * 1024;

    private static readonly string[] RegionSuffixes =
    [
        "_global", "_eea", "_in", "_jp", "_ru", "_id", "_tr", "_cn", "_tw"
    ];

    /// <summary>
    /// Xiaomi bootloader <c>getvar product</c> often returns storage type (SM_UFS / SM UFS), not the ROM codename.
    /// Karşılaştırma normalize edilir (boşluk/alt çizgi yok sayılır).
    /// </summary>
    private static readonly HashSet<string> GenericHardwareTokensNormalized = new(StringComparer.OrdinalIgnoreCase)
    {
        "smufs", "smemmc", "ufs", "emmc", "nand"
    };

    [GeneratedRegex(
        @"getprop\s*\(\s*[""']ro\.(?:product\.device|product\.name|build\.product)[""']\s*\)\s*==\s*[""'](?<id>[A-Za-z0-9._-]+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceAssertRegex();

    [GeneratedRegex(
        @"pre-device\s*=\s*(?<ids>[A-Za-z0-9._,-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetadataDeviceRegex();

    public static CustomRomPackageInfo Inspect(string zipPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("ROM zip dosyası bulunamadı.", zipPath);

        if (!zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Yalnızca .zip Custom ROM paketleri desteklenir.");

        using var zip = ZipFile.OpenRead(zipPath);
        var updater = FindEntry(zip, "META-INF/com/google/android/updater-script");
        var updateBinary = FindEntry(zip, "META-INF/com/google/android/update-binary");
        var metadata = FindEntry(zip, "META-INF/com/android/metadata");
        var payload = FindEntry(zip, "payload.bin");
        var dynamicList = FindEntry(zip, "dynamic_partitions_op_list");

        var scriptText = ReadEntryText(updater);
        var metadataText = ReadEntryText(metadata);
        var claimed = ParseClaimedDevices(scriptText, metadataText);

        var warnings = new List<string>();
        if (updater is null && payload is null)
            warnings.Add("updater-script ve payload.bin yok — bu dosya Custom ROM olmayabilir.");
        if (claimed.Count == 0)
            warnings.Add("Pakette cihaz kod adı doğrulaması bulunamadı. Yanlış ROM brick riski taşır.");
        if (payload is not null && updater is null)
            warnings.Add("A/B payload paketi. TWRP/OrangeFox sideload gerekir.");

        var kind = payload is not null && updater is null
            ? "A/B payload ROM"
            : updater is not null
                ? "Recovery zip ROM"
                : "Bilinmeyen zip";

        var androidMajor = DfeZipResolver.DetectAndroidMajorFromRomFileName(Path.GetFileName(zipPath));

        return new CustomRomPackageInfo
        {
            FilePath = Path.GetFullPath(zipPath),
            FileName = Path.GetFileName(zipPath),
            DetectedAndroidMajor = androidMajor,
            SizeBytes = new FileInfo(zipPath).Length,
            HasUpdaterScript = updater is not null,
            HasUpdateBinary = updateBinary is not null,
            HasPayloadBin = payload is not null,
            HasDynamicPartitionsList = dynamicList is not null,
            ClaimedDevices = claimed,
            Warnings = warnings,
            Summary = claimed.Count == 0
                ? $"{kind} · hedef cihaz belirtilmemiş"
                : $"{kind} · hedefler: {string.Join(", ", claimed)}"
        };
    }

    public static CustomRomCompatibilityResult Evaluate(CustomRomPackageInfo package, string deviceCodename)
    {
        ArgumentNullException.ThrowIfNull(package);
        var reported = deviceCodename?.Trim() ?? "";
        var claimed = package.ClaimedDevices;
        var fromFile = TryResolveCodenameFromPackage(package);
        var codename = ResolveReportedCodename(reported, package);

        // Zip / payload hedefi dosya adında net — SM_UFS gibi sahte product'ı ez.
        if (!string.IsNullOrWhiteSpace(fromFile)
            && claimed.Any(c => CodenamesMatch(c, fromFile)))
        {
            return new CustomRomCompatibilityResult
            {
                Compatibility = CustomRomCompatibility.Compatible,
                DeviceCodename = fromFile,
                ClaimedDevices = claimed,
                Reason = IsGenericHardwareToken(reported)
                    ? $"Bootloader '{reported}' (depolama) bildirdi; ROM zip hedefi '{fromFile}' — uyumlu."
                    : $"ROM bu cihazla uyumlu ({fromFile})."
            };
        }

        if (claimed.Count == 0)
        {
            return new CustomRomCompatibilityResult
            {
                Compatibility = CustomRomCompatibility.Unverifiable,
                DeviceCodename = codename,
                ClaimedDevices = claimed,
                Reason = string.IsNullOrWhiteSpace(codename)
                    ? "Cihaz kod adı okunamadı ve ROM hedef cihaz listesi boş."
                    : $"ROM '{codename}' için assert içermiyor. Yanlış paket brick yapabilir."
            };
        }

        if (string.IsNullOrWhiteSpace(codename) || IsGenericHardwareToken(codename))
        {
            return new CustomRomCompatibilityResult
            {
                Compatibility = CustomRomCompatibility.Unverifiable,
                DeviceCodename = IsGenericHardwareToken(reported) ? reported : codename,
                ClaimedDevices = claimed,
                Reason =
                    IsGenericHardwareToken(reported)
                        ? $"Bootloader kod adı yerine '{reported}' (depolama türü) döndürdü. " +
                          $"ROM hedefleri: {string.Join(", ", claimed)}. " +
                          "Xiaomi 11T Pro (vili) ve zip doğruysa devam edebilirsiniz."
                        : "Cihaz bağlı değil veya kod adı alınamadı. ROM hedefleri: " + string.Join(", ", claimed)
            };
        }

        if (claimed.Any(c => CodenamesMatch(c, codename)))
        {
            return new CustomRomCompatibilityResult
            {
                Compatibility = CustomRomCompatibility.Compatible,
                DeviceCodename = codename,
                ClaimedDevices = claimed,
                Reason = $"ROM bu cihazla uyumlu ({codename})."
            };
        }

        // SM_UFS / SM UFS asla "uyumsuz cihaz" sayılmaz — yanlış brick uyarısı.
        if (IsGenericHardwareToken(reported))
        {
            return new CustomRomCompatibilityResult
            {
                Compatibility = CustomRomCompatibility.Unverifiable,
                DeviceCodename = reported,
                ClaimedDevices = claimed,
                Reason =
                    $"Bootloader '{reported}' döndürdü (gerçek kod adı değil). " +
                    $"ROM hedefleri: {string.Join(", ", claimed)}. Zip doğruysa devam edin."
            };
        }

        return new CustomRomCompatibilityResult
        {
            Compatibility = CustomRomCompatibility.Incompatible,
            DeviceCodename = codename,
            ClaimedDevices = claimed,
            Reason = $"Bu ROM '{codename}' için değil. Hedefler: {string.Join(", ", claimed)}. Yükleme engellendi."
        };
    }

    public static bool IsGenericHardwareToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = NormalizeHardwareToken(value);
        return normalized.Length > 0 && GenericHardwareTokensNormalized.Contains(normalized);
    }

    /// <summary>Harf/rakam dışını at: "SM_UFS", "SM UFS", "sm-ufs" → smufs.</summary>
    public static string NormalizeHardwareToken(string value) =>
        string.Concat((value ?? "").Where(char.IsLetterOrDigit)).ToLowerInvariant();

    /// <summary>
    /// Fastboot <c>product=SM_UFS</c> ise ROM dosya adındaki kod adı kullanılır (ör. crDroid-…-vili-…).
    /// </summary>
    public static string ResolveReportedCodename(string? reported, CustomRomPackageInfo package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var token = reported?.Trim() ?? "";
        if (token.Length > 0 && !IsGenericHardwareToken(token))
            return token;

        var fromFile = TryResolveCodenameFromPackage(package);
        return !string.IsNullOrWhiteSpace(fromFile) ? fromFile : token;
    }

    /// <summary>ClaimedDevices + zip adı (…-vili-…) eşleşmesi.</summary>
    public static string? TryResolveCodenameFromPackage(CustomRomPackageInfo package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var fileName = package.FileName;
        if (string.IsNullOrWhiteSpace(fileName) && !string.IsNullOrWhiteSpace(package.FilePath))
            fileName = Path.GetFileName(package.FilePath);

        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        foreach (var claimed in package.ClaimedDevices)
        {
            if (string.IsNullOrWhiteSpace(claimed))
                continue;

            if (FileNameContainsCodename(fileName, claimed))
                return claimed.Trim();
        }

        return null;
    }

    private static bool FileNameContainsCodename(string fileName, string codename)
    {
        if (fileName.Contains(codename, StringComparison.OrdinalIgnoreCase))
            return true;

        // …-vili-… / …_vili_… / ….vili.…
        var needle = codename.Trim();
        if (needle.Length < 2)
            return false;

        var separators = new[] { '-', '_', '.', ' ' };
        foreach (var part in fileName.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static IReadOnlyList<string> ParseClaimedDevices(string? updaterScript, string? metadata)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        CollectFromScript(updaterScript, set);
        CollectFromMetadata(metadata, set);

        return set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool CodenamesMatch(string claimed, string device)
    {
        var a = NormalizeCodename(claimed);
        var b = NormalizeCodename(device);
        return a.Length > 0 && a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeCodename(string value)
    {
        var s = (value ?? "").Trim().ToLowerInvariant();
        foreach (var suffix in RegionSuffixes)
        {
            if (s.EndsWith(suffix, StringComparison.Ordinal))
                return s[..^suffix.Length];
        }

        return s;
    }

    private static void CollectFromScript(string? script, HashSet<string> set)
    {
        if (string.IsNullOrWhiteSpace(script))
            return;

        foreach (Match match in DeviceAssertRegex().Matches(script))
        {
            var id = match.Groups["id"].Value.Trim();
            if (id.Length > 0)
                set.Add(id);
        }
    }

    private static void CollectFromMetadata(string? metadata, HashSet<string> set)
    {
        if (string.IsNullOrWhiteSpace(metadata))
            return;

        foreach (Match match in MetadataDeviceRegex().Matches(metadata))
        {
            foreach (var part in match.Groups["ids"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part.Length > 0)
                    set.Add(part);
            }
        }
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        return zip.Entries.FirstOrDefault(e =>
            e.FullName.Replace('\\', '/').Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadEntryText(ZipArchiveEntry? entry)
    {
        if (entry is null)
            return null;
        if (entry.Length > MaxScriptBytes)
            return null;

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
