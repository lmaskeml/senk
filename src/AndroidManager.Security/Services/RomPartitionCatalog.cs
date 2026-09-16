using System.Text.RegularExpressions;
using AndroidManager.Core.Models;

namespace AndroidManager.Security.Services;

public static partial class RomPartitionCatalog
{
    private static readonly HashSet<string> KernelNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "boot", "dtbo", "init_boot", "vendor_boot", "recovery",
        "vbmeta", "vbmeta_system", "vbmeta_vendor", "vendor_kernel_boot"
    };

    private static readonly HashSet<string> RadioNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "modem", "modemst1", "modemst2", "fsg", "fsc", "nvdata", "nvram",
        "persist", "efs", "efs_backup", "md1img", "md1img_a", "md1img_b",
        "mcfg", "radio"
    };

    private static readonly HashSet<string> SuperNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "super", "system", "system_ext", "vendor", "product", "odm", "cust"
    };

    private static readonly HashSet<string> ExtraNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "abl", "xbl", "xbl_config", "xbl_sc", "splash", "logo", "dsp", "bluetooth",
        "keymaster", "tz", "hyp", "cmnlib", "cmnlib64", "devcfg", "qupfw",
        "imagefv", "uefisecapp", "featenabler", "aop", "aop_config", "shrm",
        "cpucp", "multiimgqti", "rtice", "spunvm", "misc", "metadata"
    };

    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "userdata", "userdatasd", "cache", "scratch", "frp"
    };

    [GeneratedRegex(@"\s(?<name>[A-Za-z0-9._+-]+)\s+->\s+(?<target>\S+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SymlinkRegex();

    [GeneratedRegex(@"^\s*\d+\s+\d+\s+(?<blocks>\d+)\s+(?<name>\S+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProcPartitionsRegex();

    public static RomBackupCategory? Classify(string rawName)
    {
        var stem = StripSlotSuffix(rawName);
        if (Forbidden.Contains(stem) || Forbidden.Contains(rawName))
            return null;
        if (KernelNames.Contains(stem))
            return RomBackupCategory.KernelRecovery;
        if (RadioNames.Contains(stem) || RadioNames.Contains(rawName))
            return RomBackupCategory.RadioImei;
        if (SuperNames.Contains(stem))
            return RomBackupCategory.SuperSystem;
        if (ExtraNames.Contains(stem))
            return RomBackupCategory.ExtraFirmware;
        return null;
    }

    public static bool IsForbidden(string name)
    {
        var stem = StripSlotSuffix(name);
        return Forbidden.Contains(stem) || Forbidden.Contains(name);
    }

    public static bool IsSlotAlias(string name, IReadOnlyCollection<string> present)
    {
        if (name.EndsWith("_a", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_b", StringComparison.OrdinalIgnoreCase))
            return false;

        return present.Contains(name + "_a", StringComparer.OrdinalIgnoreCase)
               || present.Contains(name + "_b", StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<DevicePartitionInfo> BuildMap(string lsOutput, string? procPartitions)
    {
        var links = ParseByNameListing(lsOutput);
        var sizes = ParseProcPartitions(procPartitions);
        var names = links.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var list = new List<DevicePartitionInfo>();
        foreach (var (name, target) in links.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var category = Classify(name);
            if (category is null)
                continue;

            var kernel = Path.GetFileName(target.Replace('\\', '/'));
            sizes.TryGetValue(kernel, out var size);
            list.Add(new DevicePartitionInfo
            {
                Name = name,
                BlockPath = "/dev/block/by-name/" + name,
                KernelBlock = kernel,
                SizeBytes = size,
                Category = category.Value,
                IsSlotAlias = IsSlotAlias(name, names)
            });
        }

        return list;
    }

    public static IReadOnlyDictionary<string, string> ParseByNameListing(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(output))
            return map;

        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            var link = SymlinkRegex().Match(line);
            if (link.Success)
            {
                map[link.Groups["name"].Value] = link.Groups["target"].Value;
                continue;
            }

            if (line.Contains("->", StringComparison.Ordinal))
                continue;

            var token = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (!string.IsNullOrWhiteSpace(token)
                && token.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '+'))
            {
                map.TryAdd(token, "/dev/block/by-name/" + token);
            }
        }

        return map;
    }

    public static IReadOnlyDictionary<string, long> ParseProcPartitions(string? output)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(output))
            return map;

        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = ProcPartitionsRegex().Match(raw);
            if (!match.Success)
                continue;

            if (!long.TryParse(match.Groups["blocks"].Value, out var blocks))
                continue;

            map[match.Groups["name"].Value] = blocks * 1024;
        }

        return map;
    }

    public static string StripSlotSuffix(string name)
    {
        if (name.EndsWith("_a", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_b", StringComparison.OrdinalIgnoreCase))
            return name[..^2];
        return name;
    }
}
