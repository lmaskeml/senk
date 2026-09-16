using System.Text.RegularExpressions;

namespace AndroidManager.Security.Services;

/// <summary>Disable Force Encrypt (DFE) zip — ROM Android sürümüne göre tools/dfe veya kullanıcı yolu.</summary>
public static partial class DfeZipResolver
{
    [GeneratedRegex(
        @"(?:Android|android)[-_.]?(\d{1,2})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AndroidTokenRegex();

    [GeneratedRegex(
        @"(?<![0-9])(?<major>1[0-6]|[0-9])\.0(?![0-9])",
        RegexOptions.CultureInvariant)]
    private static partial Regex MajorDotZeroRegex();

    public static int? DetectAndroidMajorFromRomFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        foreach (Match match in AndroidTokenRegex().Matches(fileName))
        {
            if (int.TryParse(match.Groups[1].Value, out var major) && major is >= 8 and <= 16)
                return major;
        }

        foreach (Match match in MajorDotZeroRegex().Matches(fileName))
        {
            if (int.TryParse(match.Groups["major"].Value, out var major) && major is >= 8 and <= 16)
                return major;
        }

        return null;
    }

    public static string GetBundledDfeDirectory()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        foreach (var dir in new[]
                 {
                     Path.Combine(baseDir, "tools", "dfe"),
                     FindRepoToolsDirectory(baseDir) is { } repo ? Path.Combine(repo, "dfe") : "",
                 })
        {
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                return dir;
        }

        return Path.Combine(baseDir, "tools", "dfe");
    }

    public static string? ResolveDfeZip(int? androidMajor, string? userSelectedPath)
    {
        if (!string.IsNullOrWhiteSpace(userSelectedPath) && File.Exists(userSelectedPath))
            return Path.GetFullPath(userSelectedPath);

        if (androidMajor is null or < 8)
            return null;

        var dir = GetBundledDfeDirectory();
        if (!Directory.Exists(dir))
            return null;

        var patterns = new[]
        {
            $"*android*{androidMajor}*dfe*.zip",
            $"*dfe*android*{androidMajor}*.zip",
            $"*a{androidMajor}*dfe*.zip",
            $"*{androidMajor}*disable*encrypt*.zip",
            $"*DFE*{androidMajor}*.zip",
            "*dfe*.zip",
            "*DFE*.zip",
        };

        foreach (var pattern in patterns)
        {
            var hit = Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => ScoreDfeMatch(f, androidMajor.Value))
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (hit is not null)
                return hit;
        }

        return null;
    }

    public static bool RecommendFormatDataOnly(int? androidMajor) => androidMajor is >= 15;

    public static string DescribeStrategy(int? androidMajor, string? resolvedDfePath)
    {
        if (RecommendFormatDataOnly(androidMajor))
        {
            return androidMajor is not null
                ? $"Android {androidMajor}: Force Encrypt genelde Format Data ile çözülür (A15+ DFE nadiren gerekir)."
                : "Format Data (FBE sıfırlama) önerilir.";
        }

        if (!string.IsNullOrWhiteSpace(resolvedDfePath))
            return $"Android {androidMajor}: DFE zip bulundu — kurulumdan önce flaşlanabilir, ardından Format Data.";

        return androidMajor is not null
            ? $"Android {androidMajor}: tools/dfe/ altına DFE zip koyun veya Format Data kullanın."
            : "Format Data veya cihaza uygun DFE zip.";
    }

    private static int ScoreDfeMatch(string path, int androidMajor)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        var score = 0;
        if (name.Contains("dfe", StringComparison.Ordinal))
            score += 10;
        if (name.Contains($"android{androidMajor}", StringComparison.Ordinal)
            || name.Contains($"android-{androidMajor}", StringComparison.Ordinal)
            || name.Contains($"a{androidMajor}", StringComparison.Ordinal)
            || name.Contains($"{androidMajor}", StringComparison.Ordinal))
            score += 20;
        if (name.Contains("disable", StringComparison.Ordinal) && name.Contains("encrypt", StringComparison.Ordinal))
            score += 5;
        return score;
    }

    private static string? FindRepoToolsDirectory(string startDir)
    {
        try
        {
            var dir = new DirectoryInfo(startDir);
            for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent!)
            {
                var tools = Path.Combine(dir.FullName, "tools");
                if (Directory.Exists(tools))
                    return tools;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }
}
