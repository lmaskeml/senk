using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using AndroidManager.Core.Models;

namespace AndroidManager.Security.Root;

public static class MagiskModulePropParser
{
    private static readonly Regex IdPattern = new(
        @"^[a-zA-Z][a-zA-Z0-9._-]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static MagiskModuleInfo? TryParse(string? propText, string path = "")
    {
        if (string.IsNullOrWhiteSpace(propText))
            return null;

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in propText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            fields[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }

        fields.TryGetValue("id", out var id);
        if (!IsValidId(id))
            id = IdFromPath(path);
        if (!IsValidId(id))
            return null;

        fields.TryGetValue("name", out var name);
        fields.TryGetValue("version", out var version);
        fields.TryGetValue("versionCode", out var versionCode);
        fields.TryGetValue("author", out var author);
        fields.TryGetValue("description", out var description);

        return new MagiskModuleInfo
        {
            Id = id ?? "",
            Name = string.IsNullOrWhiteSpace(name) ? id ?? "" : name,
            Version = version ?? "",
            VersionCode = versionCode ?? "",
            Author = author ?? "",
            Description = description ?? "",
            Path = path,
            IsEnabled = true
        };
    }

    public static MagiskModuleZipInfo? TryReadZip(string zipPath)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            return null;

        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').Equals("module.prop", StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return null;

            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var parsed = TryParse(reader.ReadToEnd());
            if (parsed is null)
                return null;

            return new MagiskModuleZipInfo
            {
                Id = parsed.Id,
                Name = parsed.DisplayName,
                Version = parsed.Version,
                Author = parsed.Author,
                Description = parsed.Description,
                FilePath = zipPath
            };
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static IReadOnlyList<MagiskModuleInfo> ParseDeviceDump(string dump)
    {
        var list = new List<MagiskModuleInfo>();
        if (string.IsNullOrWhiteSpace(dump))
            return list;

        var blocks = dump.Split("AM_BEGIN", StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            var end = block.IndexOf("AM_END", StringComparison.Ordinal);
            var body = end >= 0 ? block[..end] : block;

            var path = "";
            var disabled = false;
            var remove = false;
            var update = false;
            var pending = false;
            var prop = new StringBuilder();

            foreach (var raw in body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.StartsWith("AM_PATH=", StringComparison.Ordinal))
                    path = line["AM_PATH=".Length..];
                else if (line.StartsWith("AM_DISABLED=", StringComparison.Ordinal))
                    disabled = line.EndsWith('1');
                else if (line.StartsWith("AM_REMOVE=", StringComparison.Ordinal))
                    remove = line.EndsWith('1');
                else if (line.StartsWith("AM_UPDATE=", StringComparison.Ordinal))
                    update = line.EndsWith('1');
                else if (line.StartsWith("AM_PENDING=", StringComparison.Ordinal))
                    pending = line.EndsWith('1');
                else if (!line.StartsWith("AM_", StringComparison.Ordinal))
                    prop.AppendLine(line);
            }

            var parsed = TryParse(prop.ToString(), path);
            if (parsed is null)
                continue;

            list.Add(new MagiskModuleInfo
            {
                Id = parsed.Id,
                Name = parsed.Name,
                Version = parsed.Version,
                VersionCode = parsed.VersionCode,
                Author = parsed.Author,
                Description = parsed.Description,
                Path = path,
                IsEnabled = !disabled && !remove,
                RemovePending = remove,
                UpdatePending = update,
                InstallPending = pending
            });
        }

        return list
            .GroupBy(m => m.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static bool IsInstallSuccess(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return false;

        if (LooksFailed(output))
            return false;

        return output.Contains("- Done", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("\nDone", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("installed", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("Installation complete", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksFailed(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return false;

        return output.Contains("not a Magisk module", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("Installation failed", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("failed to", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("No such file", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && IdPattern.IsMatch(id);

    public static string IdFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var slash = normalized.LastIndexOf('/');
        var folder = slash >= 0 ? normalized[(slash + 1)..] : normalized;
        return IsValidId(folder) ? folder : "";
    }
}
