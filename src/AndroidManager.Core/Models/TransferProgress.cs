namespace AndroidManager.Core.Models;

public sealed class TransferProgress
{
    public string FileName { get; init; } = string.Empty;
    public long BytesTransferred { get; init; }
    public long TotalBytes { get; init; }
    public string Speed { get; init; } = string.Empty;

    /// <summary>Optional "3/12" style counter for multi-file transfers.</summary>
    public string FileCounter { get; init; } = string.Empty;

    public bool IsIndeterminate => TotalBytes <= 0;

    public int Percentage => TotalBytes > 0
        ? (int)Math.Clamp(BytesTransferred * 100 / TotalBytes, 0, 100)
        : 0;

    public string PercentLabel => IsIndeterminate ? "…" : $"{Percentage}%";

    public string DetailText
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(FileCounter))
                parts.Add(FileCounter);
            if (TotalBytes > 0)
                parts.Add($"{FormatBytes(BytesTransferred)} / {FormatBytes(TotalBytes)}");
            if (!string.IsNullOrWhiteSpace(Speed))
                parts.Add(Speed);
            return string.Join(" · ", parts);
        }
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1_048_576 => $"{bytes / 1024.0:F1} KB",
        < 1_073_741_824 => $"{bytes / 1_048_576.0:F1} MB",
        _ => $"{bytes / 1_073_741_824.0:F2} GB"
    };
}
