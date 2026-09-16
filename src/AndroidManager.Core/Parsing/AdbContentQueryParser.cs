using System.Text.RegularExpressions;

namespace AndroidManager.Core.Parsing;

public static partial class AdbContentQueryParser
{
    public static IReadOnlyList<Dictionary<string, string>> ParseRows(string raw)
    {
        var rows = new List<Dictionary<string, string>>();
        if (string.IsNullOrWhiteSpace(raw))
            return rows;

        foreach (var line in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("Row:", StringComparison.OrdinalIgnoreCase))
                continue;

            var idx = line.IndexOf(' ');
            var payload = idx >= 0 ? line[(idx + 1)..].Trim() : line;

            var firstEq = payload.IndexOf('=');
            if (firstEq > 0)
            {
                var keyStart = firstEq;
                while (keyStart > 0)
                {
                    var c = payload[keyStart - 1];
                    if (char.IsLetterOrDigit(c) || c == '_')
                        keyStart--;
                    else
                        break;
                }

                payload = payload[keyStart..];
            }

            var dict = ParseFields(payload);
            if (dict.Count > 0)
                rows.Add(dict);
        }

        return rows;
    }

    public static Dictionary<string, string> ParseFields(string payload)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matches = FieldKeyRegex().Matches(payload);
        for (var i = 0; i < matches.Count; i++)
        {
            var key = matches[i].Groups[1].Value;
            var valueStart = matches[i].Index + matches[i].Length;
            var valueEnd = i + 1 < matches.Count ? matches[i + 1].Index : payload.Length;
            var value = payload[valueStart..valueEnd].Trim().TrimEnd(',').Trim();
            if (value.Equals("NULL", StringComparison.OrdinalIgnoreCase))
                value = string.Empty;
            dict[key] = value;
        }

        return dict;
    }

    [GeneratedRegex(@"([A-Za-z_][A-Za-z0-9_]*)=")]
    private static partial Regex FieldKeyRegex();
}
