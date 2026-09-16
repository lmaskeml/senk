using System.IO;

namespace AndroidManager.Files.Services;

public static class FileIconResolver
{
    private static readonly Dictionary<string, string> ExtMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "🖼", [".jpeg"] = "🖼", [".png"] = "🖼",
        [".gif"] = "🖼", [".bmp"] = "🖼", [".webp"] = "🖼",
        [".mp4"] = "🎬", [".mkv"] = "🎬", [".avi"] = "🎬",
        [".mov"] = "🎬", [".webm"] = "🎬",
        [".mp3"] = "🎵", [".flac"] = "🎵", [".wav"] = "🎵",
        [".aac"] = "🎵", [".ogg"] = "🎵",
        [".pdf"] = "📄", [".doc"] = "📝", [".docx"] = "📝",
        [".xls"] = "📊", [".xlsx"] = "📊", [".ppt"] = "📊",
        [".txt"] = "📄", [".csv"] = "📊",
        [".json"] = "⚙", [".xml"] = "⚙", [".yaml"] = "⚙",
        [".cs"] = "💻", [".py"] = "💻", [".js"] = "💻",
        [".zip"] = "🗜", [".rar"] = "🗜", [".7z"] = "🗜",
        [".tar"] = "🗜", [".gz"] = "🗜",
        [".apk"] = "📦", [".aab"] = "📦", [".obb"] = "📦",
        [".xapk"] = "📦", [".apks"] = "📦", [".apkm"] = "📦",
    };

    public static string GetEmoji(string fileName, bool isDirectory) =>
        isDirectory
            ? "📁"
            : ExtMap.TryGetValue(Path.GetExtension(fileName), out var icon)
                ? icon
                : "📄";

    public static bool IsImage(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp";
    }
}
