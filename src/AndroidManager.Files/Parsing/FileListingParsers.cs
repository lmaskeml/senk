using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;
using AndroidManager.Core.Models;

namespace AndroidManager.Files.Parsing;

public static partial class FileListingParsers
{
    public static IReadOnlyList<FileSystemItem> ParseStatOutput(string raw, string basePath)
    {
        var items = new List<FileSystemItem>();
        foreach (var line in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|');
            if (parts.Length < 4) continue;

            var type = parts[0].Trim();
            _ = long.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
            _ = long.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch);
            var fullPath = parts[3].Trim();
            var name = Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(name) || name is "." or "..") continue;

            items.Add(new FileSystemItem
            {
                Name = name,
                FullPath = fullPath,
                IsDirectory = type.Contains("directory", StringComparison.OrdinalIgnoreCase),
                IsSymlink = type.Contains("symbolic", StringComparison.OrdinalIgnoreCase),
                Size = size,
                Modified = epoch > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime
                    : default
            });
        }

        return Sort(items);
    }

    public static IReadOnlyList<FileSystemItem> ParseLsOutput(string raw, string basePath)
    {
        var items = new List<FileSystemItem>();
        var normalizedBase = basePath.TrimEnd('/');

        foreach (Match match in LsLineRegex().Matches(raw))
        {
            var perms = match.Groups[1].Value;
            var name = match.Groups[4].Value.Trim();
            if (name is "." or "..") continue;

            // Symlink target: "name -> target"
            if (name.Contains(" -> ", StringComparison.Ordinal))
                name = name.Split(" -> ", 2)[0];

            _ = long.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
            _ = DateTime.TryParse(match.Groups[3].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var modified);

            items.Add(new FileSystemItem
            {
                Name = name,
                FullPath = $"{normalizedBase}/{name}",
                IsDirectory = perms[0] == 'd',
                IsSymlink = perms[0] == 'l',
                Size = size,
                Modified = modified,
                Permissions = perms
            });
        }

        return Sort(items);
    }

    private static IReadOnlyList<FileSystemItem> Sort(List<FileSystemItem> items) =>
        items.OrderByDescending(i => i.IsDirectory)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    [GeneratedRegex(
        @"^([dl\-rwxstST]{10})\s+\d+\s+\S+\s+\S+\s+(\d+)\s+((?:\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2})|(?:[A-Za-z]{3}\s+\d{1,2}\s+\d{2}:\d{2})|(?:[A-Za-z]{3}\s+\d{1,2}\s+\d{4}))\s+(.+)$",
        RegexOptions.Multiline)]
    private static partial Regex LsLineRegex();
}
