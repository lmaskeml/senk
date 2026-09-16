namespace AndroidManager.Core.Models;

public enum GalleryMediaKind
{
    Image,
    Video
}

public enum MediaSortOrder
{
    DateDesc,
    DateAsc,
    NameAsc,
    SizeDesc
}

public sealed class GalleryMediaItem
{
    public long Id { get; init; }
    public string RemotePath { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string MimeType { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public long DateTakenMs { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public GalleryMediaKind Kind { get; init; }

    /// <summary>MediaStore bucket / album display name.</summary>
    public string Album { get; init; } = string.Empty;

    /// <summary>Video duration in milliseconds (0 for photos).</summary>
    public long DurationMs { get; init; }

    public DateTime DateTakenLocal => DateTimeOffset.FromUnixTimeMilliseconds(
            DateTakenMs > 0 ? DateTakenMs : 0)
        .LocalDateTime;

    public string SizeFormatted => SizeBytes switch
    {
        < 1_048_576 => $"{SizeBytes / 1024.0:F0} KB",
        < 1_073_741_824 => $"{SizeBytes / 1_048_576.0:F1} MB",
        _ => $"{SizeBytes / 1_073_741_824.0:F2} GB"
    };

    public string DurationFormatted => DurationMs > 0
        ? TimeSpan.FromMilliseconds(DurationMs).ToString(@"mm\:ss")
        : string.Empty;

    public string ResolutionFormatted => Width > 0
        ? $"{Width}×{Height}"
        : string.Empty;

    public bool HasRemotePath => !string.IsNullOrWhiteSpace(RemotePath);
    public bool IsVideo => Kind == GalleryMediaKind.Video;
}

public sealed class MediaAlbum
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public int Count { get; init; }
    public string CoverPath { get; init; } = string.Empty;
}
