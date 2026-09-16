namespace AndroidManager.Core.Services;

/// <summary>
/// Xiaomi vili (11T Pro) A/B partition adları.
/// getvar all: vendor_boot_a/b var — vendor_boot_ab YOK (bat _ab bazı sürümlerde çalışır).
/// </summary>
public static class XiaomiAbFlashProfile
{
    private static readonly HashSet<string> AbSlotStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "boot", "xbl", "xbl_config", "abl", "aop", "tz", "featenabler", "hyp", "modem",
        "bluetooth", "dsp", "keymaster", "devcfg", "qupfw", "uefisecapp", "imagefv",
        "shrm", "multiimgoem", "cpucp", "qweslicstore", "vendor_boot", "dtbo",
        "vbmeta", "vbmeta_system", "vbmeta_vendor", "init_boot"
    };

    private static readonly HashSet<string> DualSlotMirrorStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "boot", "vendor_boot", "dtbo", "vbmeta", "vbmeta_system", "init_boot"
    };

    /// <summary>Stock flash_all.bat firmware — custom ROM payload'da YAZILMAZ.</summary>
    public static readonly string[] BootloaderFirmwareOrder =
    [
        "xbl", "xbl_config", "abl", "aop", "tz", "featenabler", "hyp", "modem",
        "bluetooth", "dsp", "keymaster", "devcfg", "qupfw", "uefisecapp", "imagefv",
        "shrm", "cpucp"
    ];

    private static readonly HashSet<string> CustomRomFirmwareSkip = new(
        BootloaderFirmwareOrder.Concat(["multiimgoem", "qweslicstore", "logfs", "rescue", "misc",
            "storsec", "spunvm", "mdcompress", "rtice", "metadata"]),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>super içinde — ayrı flash resize hatası verir (Xiaomi fastbootd).</summary>
    public static readonly string[] SuperLogicalPartitions =
    [
        "system", "system_ext", "vendor", "product", "odm"
    ];

    /// <summary>Fastbootd'de öncelik: super (veya lpmake ile üretilen super).</summary>
    public static readonly string[] FastbootdSuperOrder =
    [
        "super"
    ];

    public static readonly string[] FastbootdExtraOrder =
    [
        "cust"
    ];

    /// <summary>super yoksa (lpmake/update başarısız) son çare — resize riski var.</summary>
    public static readonly string[] FastbootdDynamicOrder =
    [
        "system", "system_ext", "vendor", "product", "odm"
    ];

    /// <summary>Normal fastboot'ta ilk adım — fastbootd öncesi AVB kapat.</summary>
    public static readonly string[] BeforeFastbootdVbmetaOrder =
    [
        "vbmeta", "vbmeta_system"
    ];

    /// <summary>vbmeta sonrası, fastbootd girişinden önce boot imajları.</summary>
    public static readonly string[] BeforeFastbootdBootstrapOrder =
    [
        "vendor_boot", "boot"
    ];

    /// <summary>Stock flash_all.bat: dtbo → vendor_boot → vbmeta → boot (en sonda).</summary>
    public static readonly string[] FinalBootloaderOrder =
    [
        "dtbo", "vendor_boot", "vbmeta", "vbmeta_system", "boot", "init_boot"
    ];

    public static bool IsViliDevice(string? codename, string? fastbootProduct) =>
        ContainsVili(codename) || ContainsVili(fastbootProduct);

    public static bool ContainsVili(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains("vili", StringComparison.OrdinalIgnoreCase);

    public static bool ShouldSkipForCustomRom(string partitionName) =>
        CustomRomFirmwareSkip.Contains(StripSlotSuffix(partitionName));

    public static IReadOnlyList<string> ListSkippedFirmware(IReadOnlyDictionary<string, string> imageMap) =>
        imageMap.Keys.Where(ShouldSkipForCustomRom).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool UseAvbDisableFlags(string partitionName)
    {
        var stem = StripSlotSuffix(partitionName);
        return stem is "vbmeta" or "vbmeta_system";
    }

    public static bool IsSlotPartition(string partitionName) =>
        AbSlotStems.Contains(StripSlotSuffix(partitionName));

    public static bool ShouldMirrorToOtherSlot(string partitionName) =>
        DualSlotMirrorStems.Contains(StripSlotSuffix(partitionName));

    /// <summary>Aktif slota göre vendor_boot_a — _ab değil.</summary>
    public static string ResolveFlashTarget(string partitionName, string? activeSlot = "a")
    {
        if (string.IsNullOrWhiteSpace(partitionName))
            return partitionName;

        if (partitionName.EndsWith("_a", StringComparison.OrdinalIgnoreCase)
            || partitionName.EndsWith("_b", StringComparison.OrdinalIgnoreCase))
            return partitionName;

        if (partitionName.EndsWith("_ab", StringComparison.OrdinalIgnoreCase))
        {
            var stem = StripSlotSuffix(partitionName);
            return $"{stem}_{NormalizeSlot(activeSlot)}";
        }

        var stemName = StripSlotSuffix(partitionName);
        return IsSlotPartition(stemName)
            ? $"{stemName}_{NormalizeSlot(activeSlot)}"
            : partitionName;
    }

    public static IReadOnlyList<string> GetFlashTargetCandidates(string partitionName, string? activeSlot)
    {
        var stem = StripSlotSuffix(partitionName);
        if (!IsSlotPartition(stem))
            return [partitionName];

        var slot = NormalizeSlot(activeSlot);
        var candidates = new List<string>();

        void Add(string? target)
        {
            if (string.IsNullOrWhiteSpace(target))
                return;
            if (candidates.Any(c => c.Equals(target, StringComparison.OrdinalIgnoreCase)))
                return;
            candidates.Add(target);
        }

        // Stock flash_all.bat: vbmeta_ab — Xiaomi fastboot her iki slota yazar.
        if (stem.StartsWith("vbmeta", StringComparison.OrdinalIgnoreCase))
            Add($"{stem}_ab");

        Add($"{stem}_{slot}");
        Add($"{stem}_a");
        Add($"{stem}_b");
        Add($"{stem}_ab");
        Add(stem);

        return candidates;
    }

    public static string? GetMirrorSlotTarget(string successfulTarget)
    {
        if (successfulTarget.EndsWith("_a", StringComparison.OrdinalIgnoreCase))
            return string.Concat(successfulTarget.AsSpan(0, successfulTarget.Length - 2), "_b");
        if (successfulTarget.EndsWith("_b", StringComparison.OrdinalIgnoreCase))
            return string.Concat(successfulTarget.AsSpan(0, successfulTarget.Length - 2), "_a");
        return null;
    }

    public static string StripSlotSuffix(string name)
    {
        if (name.EndsWith("_ab", StringComparison.OrdinalIgnoreCase))
            return name[..^3];
        if (name.EndsWith("_a", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_b", StringComparison.OrdinalIgnoreCase))
            return name[..^2];
        return name;
    }

    public static List<(string Partition, string FlashTarget, string Path)> BuildPlan(
        IReadOnlyList<string> order,
        IReadOnlyDictionary<string, string> imageMap,
        string? activeSlot = "a")
    {
        var plan = new List<(string, string, string)>();

        foreach (var partition in order)
        {
            if (!imageMap.TryGetValue(partition, out var path) || !File.Exists(path))
                continue;

            plan.Add((partition, ResolveFlashTarget(partition, activeSlot), path));
        }

        return plan;
    }

    /// <summary>Fastbootd planı — super varsa tekil system/vendor flash'lanmaz.</summary>
    public static List<(string Partition, string FlashTarget, string Path)> BuildFastbootdPlan(
        IReadOnlyDictionary<string, string> imageMap,
        string? superImagePathOverride = null)
    {
        var plan = new List<(string, string, string)>();

        if (!string.IsNullOrWhiteSpace(superImagePathOverride) && File.Exists(superImagePathOverride))
        {
            plan.Add(("super", "super", superImagePathOverride));
        }
        else if (imageMap.TryGetValue("super", out var superPath) && File.Exists(superPath))
        {
            plan.Add(("super", "super", superPath));
        }
        else
        {
            return BuildPlan(FastbootdDynamicOrder, imageMap, activeSlot: null);
        }

        foreach (var partition in FastbootdExtraOrder)
        {
            if (imageMap.TryGetValue(partition, out var path) && File.Exists(path))
                plan.Add((partition, partition, path));
        }

        return plan;
    }

    public static bool IsSuperLogicalPartition(string partitionName) =>
        SuperLogicalPartitions.Contains(StripSlotSuffix(partitionName));

    public static List<(string Partition, string FlashTarget, string Path)> BuildRemainingPlan(
        IReadOnlyDictionary<string, string> imageMap,
        IReadOnlySet<string> alreadyPlanned,
        bool wipeUserData,
        string? activeSlot = "a")
    {
        var plan = new List<(string, string, string)>();
        foreach (var (partition, path) in imageMap.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (alreadyPlanned.Contains(partition))
                continue;
            if (ShouldSkipForCustomRom(partition))
                continue;
            if (partition.Equals("userdata", StringComparison.OrdinalIgnoreCase) && !wipeUserData)
                continue;
            if (!File.Exists(path))
                continue;

            plan.Add((partition, ResolveFlashTarget(partition, activeSlot), path));
        }

        return plan;
    }

    private static string NormalizeSlot(string? slot)
    {
        slot = slot?.Trim().ToLowerInvariant();
        return slot is "a" or "b" ? slot : "a";
    }
}
