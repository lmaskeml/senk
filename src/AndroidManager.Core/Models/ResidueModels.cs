namespace AndroidManager.Core.Models;

public enum ResidueCategory
{
    OrphanedData,
    LeftoverApk,
    AppCache,
    TempFiles,
    LogFiles,
    DalvikCache,
    EmptyDirectory,
    Thumbnails,
    DownloadJunk,
    AppLeftovers
}

public sealed class ResidueItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public ResidueCategory Category { get; init; }
    public long SizeBytes { get; init; }
    public bool RequiresRoot { get; init; }
    public string Description { get; init; } = "";

    public string CategoryLabel => Category switch
    {
        ResidueCategory.OrphanedData => "Yetim veri",
        ResidueCategory.LeftoverApk => "APK kalıntısı",
        ResidueCategory.AppCache => "Önbellek",
        ResidueCategory.TempFiles => "Geçici dosya",
        ResidueCategory.LogFiles => "Log kalıntısı",
        ResidueCategory.DalvikCache => "Dalvik önbelleği",
        ResidueCategory.EmptyDirectory => "Boş dizin",
        ResidueCategory.Thumbnails => "Küçük resim",
        ResidueCategory.DownloadJunk => "İndirme kalıntısı",
        ResidueCategory.AppLeftovers => "Uygulama kalıntısı",
        _ => "Diğer"
    };
}

public sealed class ResidueScanOptions
{
    public bool ScanOrphanedData { get; init; } = true;
    public bool ScanLeftoverApks { get; init; } = true;
    public bool ScanAppCaches { get; init; } = true;
    public bool ScanTempFiles { get; init; } = true;
    public bool ScanLogFiles { get; init; } = true;
    public bool ScanEmptyDirs { get; init; } = true;
    public bool ScanThumbnails { get; init; } = true;
    public bool ScanDownloadJunk { get; init; } = true;
    public bool DeepScan { get; init; }

    public List<string> WhitelistedPackages { get; init; } = [];
}

public sealed class ResidueScanResult
{
    public List<ResidueItem> Items { get; init; } = [];
    public bool RootAvailable { get; init; }
    public bool RootActive { get; init; }
    public TimeSpan Duration { get; init; }
    public int ScannedEntries { get; init; }

    public long TotalSize => Items.Sum(i => i.SizeBytes);

    public int CategoryCount(ResidueCategory category) =>
        Items.Count(i => i.Category == category);

    public string Summary
    {
        get
        {
            if (Items.Count == 0)
                return $"Kalıntı bulunamadı ({Duration:mm\\:ss})";
            return $"{Items.Count} kalıntı • {SizeFormatter.Format(TotalSize)} • {Duration:mm\\:ss}";
        }
    }
}

public sealed class ResidueCleanResult
{
    public ResidueItem Item { get; init; } = null!;
    public bool Success { get; init; }
    public string Message { get; init; } = "";
}

public static class SizeFormatter
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{size:0} {units[unit]}"
            : $"{size:0.0} {units[unit]}";
    }
}
