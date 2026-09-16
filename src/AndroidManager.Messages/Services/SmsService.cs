using System.Globalization;
using System.IO;
using System.Text;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Core.Parsing;
using Serilog;

namespace AndroidManager.Messages.Services;

public sealed class SmsService : ISmsService
{
    private readonly IAdbService _adb;
    private readonly IContactsService _contacts;
    private readonly ILogger _logger;

    public SmsService(IAdbService adb, IContactsService contacts, ILogger? logger = null)
    {
        _adb = adb;
        _contacts = contacts;
        _logger = logger ?? Log.ForContext<SmsService>();
    }

    public async Task<IReadOnlyList<SmsMessage>> GetMessagesAsync(
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        EnsureDevice();
        var take = Math.Clamp(limit, 1, 2000);
        const string projection = "_id:thread_id:address:body:date:type:read";

        var raw = await QuerySmsWithFallbacksAsync(projection, cancellationToken)
            .ConfigureAwait(false);

        var messages = AdbContentQueryParser.ParseRows(raw)
            .Select(ParseMessage)
            .Where(m => m is not null)
            .Cast<SmsMessage>()
            .GroupBy(m => m.Id)
            .Select(g => g.First())
            .OrderByDescending(m => m.Date)
            .Take(take)
            .ToList();

        return messages;
    }

    public async Task<IReadOnlyList<SmsThread>> GetThreadsAsync(
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        var messages = await GetMessagesAsync(limit, cancellationToken).ConfigureAwait(false);
        var threads = messages
            .Where(m => !string.IsNullOrWhiteSpace(m.Address))
            .GroupBy(m => PhoneNumberNormalizer.Normalize(m.Address), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var ordered = g.OrderByDescending(m => m.Date).ToList();
                var last = ordered[0];
                return new SmsThread
                {
                    Address = last.Address,
                    DisplayName = last.Address,
                    LastBody = last.Body,
                    LastDate = last.Date,
                    MessageCount = ordered.Count,
                    UnreadCount = ordered.Count(m => m.IsIncoming && !m.IsRead)
                };
            })
            .OrderByDescending(t => t.LastDate)
            .ToList();

        await EnrichWithContactNamesAsync(threads, cancellationToken).ConfigureAwait(false);
        return threads;
    }

    public async Task<IReadOnlyList<SmsMessage>> GetThreadMessagesAsync(
        string address,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var key = PhoneNumberNormalizer.Normalize(address);
        var all = await GetMessagesAsync(Math.Max(limit * 3, 300), cancellationToken).ConfigureAwait(false);
        return all
            .Where(m => PhoneNumberNormalizer.TryMatch(m.Address, key))
            .OrderBy(m => m.Date)
            .TakeLast(Math.Clamp(limit, 1, 500))
            .ToList();
    }

    public async Task<IReadOnlyList<SmsMessage>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        EnsureDevice();
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<SmsMessage>();

        var q = query.Trim();

        // Prefer server-side LIKE; fall back to local filter if content provider rejects --where.
        try
        {
            var escaped = q.Replace("'", "''", StringComparison.Ordinal);
            var raw = await _adb.ExecuteShellAsync(
                    "content query --uri content://sms " +
                    "--projection _id:thread_id:address:body:date:type:read " +
                    $"--where \"body LIKE '%{escaped}%'\" " +
                    "--sort \"date DESC\"",
                    cancellationToken)
                .ConfigureAwait(false);

            if (!LooksLikeFailure(raw))
            {
                var remote = AdbContentQueryParser.ParseRows(raw)
                    .Select(ParseMessage)
                    .Where(m => m is not null)
                    .Cast<SmsMessage>()
                    .OrderByDescending(m => m.Date)
                    .Take(500)
                    .ToList();
                if (remote.Count > 0)
                    return remote;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "SMS remote search failed; using local filter");
        }

        var all = await GetMessagesAsync(2000, cancellationToken).ConfigureAwait(false);
        return all
            .Where(m =>
                m.Body.Contains(q, StringComparison.OrdinalIgnoreCase)
                || m.Address.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.Date)
            .Take(500)
            .ToList();
    }

    public async Task<string> ExportToJsonAsync(
        string savePath,
        CancellationToken cancellationToken = default)
    {
        EnsureDevice();
        var messages = await GetMessagesAsync(2000, cancellationToken).ConfigureAwait(false);
        var fullPath = ResolveExportPath(savePath, "json");

        var payload = messages.Select(m => new
        {
            m.Id,
            m.Address,
            m.Body,
            m.Date,
            DateLocal = m.DateLocal.ToString("yyyy-MM-dd HH:mm:ss"),
            m.Type,
            TypeLabel = m.TypeLabel,
            m.IsRead,
            m.ThreadId
        });

        var json = System.Text.Json.JsonSerializer.Serialize(
            payload,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        await File.WriteAllTextAsync(fullPath, json, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
        _logger.Information("SMS JSON export: {Path} ({Count})", fullPath, messages.Count);
        return fullPath;
    }

    public async Task<string> ExportToCsvAsync(
        string savePath,
        CancellationToken cancellationToken = default)
    {
        EnsureDevice();
        var messages = await GetMessagesAsync(2000, cancellationToken).ConfigureAwait(false);
        var fullPath = ResolveExportPath(savePath, "csv");

        var sb = new StringBuilder();
        sb.AppendLine("Id,Address,Date,Type,Read,Body");
        foreach (var msg in messages.OrderBy(m => m.Date))
        {
            sb.Append(msg.Id).Append(',')
                .Append(Csv(msg.Address)).Append(',')
                .Append(Csv(msg.DateLocal.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
                .Append(Csv(msg.TypeLabel)).Append(',')
                .Append(msg.IsRead ? "1" : "0").Append(',')
                .Append(Csv(msg.Body))
                .AppendLine();
        }

        await File.WriteAllTextAsync(fullPath, sb.ToString(), Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
        _logger.Information("SMS CSV export: {Path} ({Count})", fullPath, messages.Count);
        return fullPath;
    }

    public async Task<SmsSendResult> SendAsync(
        string address,
        string body,
        CancellationToken cancellationToken = default)
    {
        EnsureDevice();
        address = address.Trim();
        body = body.Trim();
        if (string.IsNullOrWhiteSpace(address))
            return new SmsSendResult { Success = false, Message = "Alıcı numarası gerekli." };
        if (string.IsNullOrWhiteSpace(body))
            return new SmsSendResult { Success = false, Message = "Mesaj boş olamaz." };

        // 1) Try privileged cmd (works on some builds / emulators).
        try
        {
            var cmd = $"cmd sms send-text-message {ShellQuote(address)} {ShellQuote(body)}";
            var output = await _adb.ExecuteShellAsync(cmd, cancellationToken).ConfigureAwait(false);
            if (!LooksLikeFailure(output)
                && !output.Contains("Unknown command", StringComparison.OrdinalIgnoreCase)
                && !output.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Information("SMS sent via cmd sms: {Address}", address);
                return new SmsSendResult
                {
                    Success = true,
                    Message = "SMS gönderildi (cmd sms)."
                };
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "cmd sms send failed");
        }

        // 2) Fallback: open SMS composer on the phone (user taps Send).
        // Android 4.4+ blocks silent SMS send unless the app is the default SMS app.
        try
        {
            var uri = "sms:" + Uri.EscapeDataString(address);
            var escapedBody = body
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);

            var am =
                $"am start -a android.intent.action.SENDTO -d {ShellQuote(uri)} " +
                $"--es sms_body \"{escapedBody}\" --ez exit_on_sent true";

            var output = await _adb.ExecuteShellAsync(am, cancellationToken).ConfigureAwait(false);
            _logger.Information("SMS composer opened on device for {Address}: {Output}", address, output.Trim());

            return new SmsSendResult
            {
                Success = true,
                RequiresUserConfirm = true,
                Message =
                    "Telefonda SMS uygulaması açıldı. Modern Android sessiz SMS göndermeyi engeller; " +
                    "cihazda Gönder’e basmanız gerekir."
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "SMS send fallback failed");
            return new SmsSendResult { Success = false, Message = ex.Message };
        }
    }

    private async Task EnrichWithContactNamesAsync(
        List<SmsThread> threads,
        CancellationToken cancellationToken)
    {
        if (threads.Count == 0)
            return;

        try
        {
            var index = await _contacts.GetPhoneDisplayNameIndexAsync(cancellationToken)
                .ConfigureAwait(false);
            if (index.Count == 0)
            {
                _logger.Debug("Rehber indeksi boş — konuşma adları numara olarak kalacak");
                return;
            }

            for (var i = 0; i < threads.Count; i++)
            {
                var t = threads[i];
                var contactName = PhoneNumberNormalizer.LookupDisplayName(index, t.Address);
                if (string.IsNullOrWhiteSpace(contactName))
                    continue;

                threads[i] = new SmsThread
                {
                    Address = t.Address,
                    DisplayName = contactName,
                    ContactName = contactName,
                    LastBody = t.LastBody,
                    LastDate = t.LastDate,
                    MessageCount = t.MessageCount,
                    UnreadCount = t.UnreadCount
                };
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Rehber eşleştirmesi atlandı — konuşmalarda numara gösterilecek");
        }
    }

    private static string ResolveExportPath(string savePath, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(savePath);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var fileName = $"sms_export_{stamp}.{extension}";

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

    private static string Csv(string value)
    {
        var escaped = (value ?? string.Empty)
            .Replace("\"", "\"\"", StringComparison.Ordinal)
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    private async Task<string> QuerySmsWithFallbacksAsync(
        string projection,
        CancellationToken cancellationToken)
    {
        string? lastDenial = null;
        var merged = new StringBuilder();
        var sawAnyRow = false;

        // 1) Normal ADB shell — tüm SMS, sonra gelen/giden.
        foreach (var uri in new[] { "content://sms", "content://sms/inbox", "content://sms/sent" })
        {
            var raw = await QuerySmsAsync(uri, projection, asRoot: false, cancellationToken)
                .ConfigureAwait(false);

            if (IsPermissionDenied(raw))
            {
                lastDenial = raw;
                _logger.Warning("SMS izin engeli ({Uri}): {Snippet}", uri, Snippet(raw));
                continue;
            }

            if (LooksLikeFailure(raw))
            {
                _logger.Debug("SMS sorgu başarısız ({Uri}): {Snippet}", uri, Snippet(raw));
                continue;
            }

            if (HasContentRows(raw))
            {
                sawAnyRow = true;
                if (merged.Length > 0)
                    merged.AppendLine();
                merged.Append(raw);

                // Tüm kutu geldiyse yeter.
                if (uri == "content://sms")
                    return raw;
            }
            else if (uri == "content://sms" && IsEmptyProviderResult(raw))
            {
                // Gerçekten boş gelen kutusu — izin var.
                return raw;
            }
        }

        if (sawAnyRow)
            return merged.ToString();

        // 2) Root (su) ile tekrar dene — MIUI/HyperOS ADB SMS’i sık kilitler.
        foreach (var uri in new[] { "content://sms", "content://sms/inbox", "content://sms/sent" })
        {
            var raw = await QuerySmsAsync(uri, projection, asRoot: true, cancellationToken)
                .ConfigureAwait(false);

            if (IsPermissionDenied(raw) || LooksLikeFailure(raw))
            {
                if (IsPermissionDenied(raw))
                    lastDenial = raw;
                continue;
            }

            if (HasContentRows(raw) || IsEmptyProviderResult(raw))
            {
                _logger.Information("SMS root üzerinden okundu ({Uri})", uri);
                return raw;
            }
        }

        // 3) Projeksiyonsuz son deneme (bazı OEM’lerde projection reddedilir).
        var bare = await QuerySmsAsync("content://sms", projection: null, asRoot: false, cancellationToken)
            .ConfigureAwait(false);
        if (!LooksLikeFailure(bare) && (HasContentRows(bare) || IsEmptyProviderResult(bare)))
            return bare;

        bare = await QuerySmsAsync("content://sms", projection: null, asRoot: true, cancellationToken)
            .ConfigureAwait(false);
        if (!LooksLikeFailure(bare) && (HasContentRows(bare) || IsEmptyProviderResult(bare)))
            return bare;

        if (lastDenial is not null || IsPermissionDenied(bare))
        {
            throw new InvalidOperationException(
                "Cihaz ADB ile SMS okumaya izin vermiyor (READ_SMS / Permission Denial). " +
                "Xiaomi HyperOS/MIUI ve birçok stok ROM bunu engeller. " +
                "Root varsa Root Modu’nda doğrulayıp Yenile’ye basın; " +
                "yoksa SMS yedeklemek için telefonun kendi dışa aktarımını veya " +
                "varsayılan SMS uygulaması yetkisi olan bir yöntemi kullanın.");
        }

        throw new InvalidOperationException(
            "SMS sorgusu sonuç vermedi. Cihaz bağlı mı ve SMS uygulaması erişilebilir mi kontrol edin.");
    }

    private async Task<string> QuerySmsAsync(
        string uri,
        string? projection,
        bool asRoot,
        CancellationToken cancellationToken)
    {
        var projectionPart = string.IsNullOrWhiteSpace(projection)
            ? string.Empty
            : $" --projection {projection}";

        var baseCmd = $"content query --uri {uri}{projectionPart}";
        var withSort = $"{baseCmd} --sort \"date DESC\"";

        async Task<string> RunAsync(string shellCommand)
        {
            if (!asRoot)
                return await _adb.ExecuteShellAsync(shellCommand, cancellationToken).ConfigureAwait(false);

            var escaped = shellCommand.Replace("'", "'\\''", StringComparison.Ordinal);
            try
            {
                var viaSu = await _adb.ExecuteShellAsync($"su -c '{escaped}'", cancellationToken)
                    .ConfigureAwait(false);
                if (!IsSuDenied(viaSu))
                    return viaSu;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "su -c SMS sorgusu başarısız");
            }

            try
            {
                return await _adb.ExecuteShellAsync($"su 0 -c '{escaped}'", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "su 0 -c SMS sorgusu başarısız");
                return string.Empty;
            }
        }

        try
        {
            return await RunAsync(withSort).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "SMS query with sort failed; retrying without sort (root={Root})", asRoot);
            return await RunAsync(baseCmd).ConfigureAwait(false);
        }
    }

    private void EnsureDevice()
    {
        if (_adb.SelectedDevice is null)
            throw new InvalidOperationException("SMS için bağlı bir cihaz seçin.");
    }

    private static bool HasContentRows(string output) =>
        output.Contains("Row:", StringComparison.OrdinalIgnoreCase);

    private static bool IsEmptyProviderResult(string output)
    {
        var t = output.Trim();
        return t.Length == 0
               || t.Contains("No result found", StringComparison.OrdinalIgnoreCase)
               || t.Equals("No rows", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPermissionDenied(string output) =>
        output.Contains("Permission Denial", StringComparison.OrdinalIgnoreCase)
        || output.Contains("SecurityException", StringComparison.OrdinalIgnoreCase)
        || output.Contains("requires android.permission.READ_SMS", StringComparison.OrdinalIgnoreCase)
        || output.Contains("requires android.permission.READ_PHONE", StringComparison.OrdinalIgnoreCase);

    private static bool IsSuDenied(string output)
    {
        var o = output.Trim();
        return o.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
               || o.Contains("not allowed", StringComparison.OrdinalIgnoreCase)
               || o.Contains("not found", StringComparison.OrdinalIgnoreCase)
               || o.Contains("Can't find", StringComparison.OrdinalIgnoreCase);
    }

    private static string Snippet(string output)
    {
        var line = output.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 160 ? line : line[..160] + "…";
    }

    private static bool LooksLikeFailure(string output) =>
        string.IsNullOrWhiteSpace(output)
        || IsPermissionDenied(output)
        || output.Contains("Exception", StringComparison.OrdinalIgnoreCase)
        || (output.Contains("Error", StringComparison.OrdinalIgnoreCase)
            && !HasContentRows(output));

    private static string ShellQuote(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";
        if (!value.Contains(' ', StringComparison.Ordinal)
            && !value.Contains('"', StringComparison.Ordinal)
            && !value.Contains('\'', StringComparison.Ordinal))
            return value;
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string NormalizeAddress(string address) =>
        PhoneNumberNormalizer.Normalize(address);

    // Kept for unit tests / callers that previously used SmsService parsers.
    public static IReadOnlyList<Dictionary<string, string>> ParseContentRows(string raw) =>
        AdbContentQueryParser.ParseRows(raw);

    public static Dictionary<string, string> ParseRowFields(string payload) =>
        AdbContentQueryParser.ParseFields(payload);

    private static SmsMessage? ParseMessage(Dictionary<string, string> row)
    {
        if (!row.TryGetValue("address", out var address) || string.IsNullOrWhiteSpace(address))
            return null;

        row.TryGetValue("body", out var body);
        row.TryGetValue("_id", out var idRaw);
        row.TryGetValue("date", out var dateRaw);
        row.TryGetValue("type", out var typeRaw);
        row.TryGetValue("read", out var readRaw);
        row.TryGetValue("thread_id", out var threadRaw);

        _ = long.TryParse(idRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id);
        _ = long.TryParse(dateRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var date);
        _ = int.TryParse(typeRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var type);
        _ = int.TryParse(threadRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var threadId);
        var isRead = readRaw is "1" or "true" or "True";

        return new SmsMessage
        {
            Id = id,
            Address = address,
            Body = body ?? string.Empty,
            Date = date,
            Type = type,
            IsRead = isRead,
            ThreadId = threadId
        };
    }
}
