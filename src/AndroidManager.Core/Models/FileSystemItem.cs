using System.Globalization;
using System.IO;

namespace AndroidManager.Core.Models;

public sealed class FileSystemItem
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public bool IsSymlink { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public string Permissions { get; init; } = string.Empty;

    public string SizeFormatted => IsDirectory ? string.Empty : FormatSize(Size);
    public string Extension => Path.GetExtension(Name).ToLowerInvariant();

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1_048_576 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:F1} KB"),
        < 1_073_741_824 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_048_576.0:F1} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_073_741_824.0:F2} GB")
    };
}
