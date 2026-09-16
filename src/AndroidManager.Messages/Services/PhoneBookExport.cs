using System.Globalization;
using System.IO;
using System.Text;
using System.Xml.Linq;

namespace AndroidManager.Messages.Services;

internal static class PhoneBookExport
{
    public static string ResolvePath(string savePath, string prefix, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(savePath);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var fileName = $"{prefix}_{stamp}.{extension}";

        if (Directory.Exists(savePath)
            || savePath.EndsWith(Path.DirectorySeparatorChar)
            || savePath.EndsWith(Path.AltDirectorySeparatorChar)
            || string.IsNullOrWhiteSpace(Path.GetExtension(savePath)))
        {
            Directory.CreateDirectory(savePath);
            return Path.Combine(savePath, fileName);
        }

        var parent = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);
        return savePath;
    }

    public static string Csv(string? value)
    {
        var escaped = (value ?? string.Empty)
            .Replace("\"", "\"\"", StringComparison.Ordinal)
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    /// <summary>Excel'in açtığı SpreadsheetML (.xls) — ekstra NuGet yok.</summary>
    public static async Task WriteExcelAsync(
        string path,
        string sheetName,
        IReadOnlyList<string> headers,
        IEnumerable<IReadOnlyList<string>> rows,
        CancellationToken cancellationToken)
    {
        XNamespace ss = "urn:schemas-microsoft-com:office:spreadsheet";
        XNamespace o = "urn:schemas-microsoft-com:office:office";
        XNamespace x = "urn:schemas-microsoft-com:office:excel";

        var sheetRows = new List<XElement>();
        sheetRows.Add(MakeRow(ss, headers));

        foreach (var row in rows)
            sheetRows.Add(MakeRow(ss, row));

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XProcessingInstruction("mso-application", "progid=\"Excel.Sheet\""),
            new XElement(ss + "Workbook",
                new XAttribute(XNamespace.Xmlns + "o", o),
                new XAttribute(XNamespace.Xmlns + "x", x),
                new XAttribute(XNamespace.Xmlns + "ss", ss),
                new XElement(ss + "Worksheet",
                    new XAttribute(ss + "Name", SanitizeSheetName(sheetName)),
                    new XElement(ss + "Table", sheetRows))));

        await using var stream = File.Create(path);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteAsync(doc.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static XElement MakeRow(XNamespace ss, IEnumerable<string> cells)
    {
        var row = new XElement(ss + "Row");
        foreach (var cell in cells)
        {
            row.Add(new XElement(ss + "Cell",
                new XElement(ss + "Data",
                    new XAttribute(ss + "Type", "String"),
                    cell ?? string.Empty)));
        }

        return row;
    }

    private static string SanitizeSheetName(string name)
    {
        var cleaned = name.Replace('\\', ' ').Replace('/', ' ').Replace('?', ' ')
            .Replace('*', ' ').Replace('[', ' ').Replace(']', ' ').Trim();
        if (cleaned.Length == 0)
            cleaned = "Sayfa1";
        return cleaned.Length <= 31 ? cleaned : cleaned[..31];
    }
}
