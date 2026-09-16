using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Core.Parsing;
using Serilog;

namespace AndroidManager.Messages.Services;

public sealed class CallLogService : ICallLogService
{
    private readonly IAdbService _adb;
    private readonly ILogger _logger;

    public CallLogService(IAdbService adb, ILogger? logger = null)
    {
        _adb = adb;
        _logger = logger ?? Log.ForContext<CallLogService>();
    }

    public async Task<IReadOnlyList<CallLogEntry>> GetCallsAsync(
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        EnsureDevice();
        var take = Math.Clamp(limit, 1, 2000);
        const string projection = "_id:number:name:date:duration:type";

        var raw = await QueryCallLogAsync(projection, asRoot: false, cancellationToken)
            .ConfigureAwait(false);

        if (IsPermissionDenied(raw) || LooksLikeFailure(raw))
        {
            _logger.Warning("Call log ADB engeli; root deneniyor");
            raw = await QueryCallLogAsync(projection, asRoot: true, cancellationToken)
                .ConfigureAwait(false);
        }

        if (IsPermissionDenied(raw))
        {
            throw new InvalidOperationException(
                "Cihaz ADB ile arama geçmişi okumaya izin vermiyor (READ_CALL_LOG). " +
                "HyperOS/MIUI gibi sistemlerde genelde engellenir; root varsa Root Modu’ndan doğrulayıp Yenile’ye basın.");
        }

        if (LooksLikeFailure(raw) && !HasRows(raw) && !IsEmptyResult(raw))
            throw new InvalidOperationException("Arama geçmişi okunamadı: " + Snippet(raw));

        return AdbContentQueryParser.ParseRows(raw)
            .Select(ParseEntry)
            .Where(e => e is not null)
            .Cast<CallLogEntry>()
            .OrderByDescending(e => e.DateMs)
            .Take(take)
            .ToList();
    }

    public async Task<IReadOnlyList<CallLogEntry>> SearchAsync(
        string query,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        var all = await GetCallsAsync(Math.Max(limit, 500), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(query))
            return all.Take(limit).ToList();

        var q = query.Trim();
        return all
            .Where(c =>
                c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Number.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.TypeLabel.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();
    }

    public async Task<string> ExportToJsonAsync(
        string savePath,
        CancellationToken cancellationToken = default)
    {
        var calls = await GetCallsAsync(2000, cancellationToken).ConfigureAwait(false);
        var path = PhoneBookExport.ResolvePath(savePath, "call_log", "json");
        var payload = calls.Select(c => new
        {
            c.Id,
            c.Number,
            c.Name,
            c.DateMs,
            DateLocal = c.DateLocal.ToString("yyyy-MM-dd HH:mm:ss"),
            c.DurationSeconds,
            Duration = c.DurationFormatted,
            c.Type,
            c.TypeLabel
        });
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public async Task<string> ExportToCsvAsync(
        string savePath,
        CancellationToken cancellationToken = default)
    {
        var calls = await GetCallsAsync(2000, cancellationToken).ConfigureAwait(false);
        var path = PhoneBookExport.ResolvePath(savePath, "call_log", "csv");
        var sb = new StringBuilder();
        sb.AppendLine("Id,Name,Number,Date,Type,DurationSeconds,Duration");
        foreach (var c in calls.OrderByDescending(x => x.DateMs))
        {
            sb.Append(c.Id).Append(',')
                .Append(PhoneBookExport.Csv(c.Name)).Append(',')
                .Append(PhoneBookExport.Csv(c.Number)).Append(',')
                .Append(PhoneBookExport.Csv(c.DateLocal.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
                .Append(PhoneBookExport.Csv(c.TypeLabel)).Append(',')
                .Append(c.DurationSeconds).Append(',')
                .Append(PhoneBookExport.Csv(c.DurationFormatted))
                .AppendLine();
        }

        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(true), cancellationToken)
            .ConfigureAwait(false);
        return path;
    }

    public async Task<string> ExportToExcelAsync(
        string savePath,
        CancellationToken cancellationToken = default)
    {
        var calls = await GetCallsAsync(2000, cancellationToken).ConfigureAwait(false);
        var path = PhoneBookExport.ResolvePath(savePath, "call_log", "xls");
        await PhoneBookExport.WriteExcelAsync(
                path,
                "Arama",
                ["Id", "Ad", "Numara", "Tarih", "Tür", "Süre (sn)", "Süre"],
                calls.OrderByDescending(x => x.DateMs).Select(c => (IReadOnlyList<string>)
                [
                    c.Id.ToString(CultureInfo.InvariantCulture),
                    c.Name,
                    c.Number,
                    c.DateLocal.ToString("yyyy-MM-dd HH:mm:ss"),
                    c.TypeLabel,
                    c.DurationSeconds.ToString(CultureInfo.InvariantCulture),
                    c.DurationFormatted
                ]),
                cancellationToken)
            .ConfigureAwait(false);
        return path;
    }

    private async Task<string> QueryCallLogAsync(
        string projection,
        bool asRoot,
        CancellationToken cancellationToken)
    {
        var withSort =
            $"content query --uri content://call_log/calls --projection {projection} --sort \"date DESC\"";
        var noSort =
            $"content query --uri content://call_log/calls --projection {projection}";

        async Task<string> RunAsync(string cmd)
        {
            if (!asRoot)
                return await _adb.ExecuteShellAsync(cmd, cancellationToken).ConfigureAwait(false);

            var escaped = cmd.Replace("'", "'\\''", StringComparison.Ordinal);
            try
            {
                var viaSu = await _adb.ExecuteShellAsync($"su -c '{escaped}'", cancellationToken)
                    .ConfigureAwait(false);
                if (!IsSuDenied(viaSu))
                    return viaSu;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "su call_log failed");
            }

            try
            {
                return await _adb.ExecuteShellAsync($"su 0 -c '{escaped}'", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                return string.Empty;
            }
        }

        try
        {
            return await RunAsync(withSort).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "call_log sort query failed");
            return await RunAsync(noSort).ConfigureAwait(false);
        }
    }

    private static CallLogEntry? ParseEntry(Dictionary<string, string> row)
    {
        if (!TryLong(row, "_id", out var id))
            return null;

        row.TryGetValue("number", out var number);
        row.TryGetValue("name", out var name);
        TryLong(row, "date", out var date);
        TryInt(row, "duration", out var duration);
        TryInt(row, "type", out var type);

        return new CallLogEntry
        {
            Id = id,
            Number = number?.Trim() ?? string.Empty,
            Name = name?.Trim() ?? string.Empty,
            DateMs = date,
            DurationSeconds = Math.Max(0, duration),
            Type = type
        };
    }

    private void EnsureDevice()
    {
        if (_adb.SelectedDevice is null)
            throw new InvalidOperationException("Arama geçmişi için bağlı bir cihaz seçin.");
    }

    private static bool TryLong(Dictionary<string, string> row, string key, out long value)
    {
        value = 0;
        return row.TryGetValue(key, out var s)
               && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryInt(Dictionary<string, string> row, string key, out int value)
    {
        value = 0;
        return row.TryGetValue(key, out var s)
               && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool HasRows(string output) =>
        output.Contains("Row:", StringComparison.OrdinalIgnoreCase);

    private static bool IsEmptyResult(string output)
    {
        var t = output.Trim();
        return t.Length == 0
               || t.Contains("No result found", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPermissionDenied(string output) =>
        output.Contains("Permission Denial", StringComparison.OrdinalIgnoreCase)
        || output.Contains("SecurityException", StringComparison.OrdinalIgnoreCase)
        || output.Contains("READ_CALL_LOG", StringComparison.OrdinalIgnoreCase);

    private static bool IsSuDenied(string output)
    {
        var o = output.Trim();
        return o.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
               || o.Contains("not allowed", StringComparison.OrdinalIgnoreCase)
               || o.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeFailure(string output) =>
        string.IsNullOrWhiteSpace(output)
        || IsPermissionDenied(output)
        || (output.Contains("Exception", StringComparison.OrdinalIgnoreCase) && !HasRows(output))
        || (output.Contains("Error", StringComparison.OrdinalIgnoreCase) && !HasRows(output));

    private static string Snippet(string output)
    {
        var line = output.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 180 ? line : line[..180] + "…";
    }
}
