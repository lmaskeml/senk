using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Core.Parsing;
using Serilog;

namespace AndroidManager.Messages.Services;

public sealed class ContactsService : IContactsService
{
    private const string MimeName = "vnd.android.cursor.item/name";
    private const string MimePhone = "vnd.android.cursor.item/phone_v2";
    private const string MimeEmail = "vnd.android.cursor.item/email_v2";

    private readonly IAdbService _adb;
    private readonly ILogger _logger;

    public ContactsService(IAdbService adb, ILogger? logger = null)
    {
        _adb = adb;
        _logger = logger ?? Log.ForContext<ContactsService>();
    }

    public async Task<IReadOnlyDictionary<string, string>> GetPhoneDisplayNameIndexAsync(
        CancellationToken cancellationToken = default)
    {
        var contacts = await GetContactsAsync(cancellationToken).ConfigureAwait(false);
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var contact in contacts)
        {
            var name = contact.DisplayName.Trim();
            if (name.Length == 0)
                continue;

            foreach (var phone in contact.AllPhoneNumbers)
            {
                foreach (var key in PhoneNumberNormalizer.IndexKeysFor(phone))
                {
                    if (!index.ContainsKey(key))
                        index[key] = name;
                }
            }
        }

        return index;
    }

    public async Task<IReadOnlyList<PhoneContact>> GetContactsAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureDevice();
        var map = new Dictionary<long, ContactAccumulator>();

        var phones = await QueryProviderWithRootFallbackAsync(
                "content://com.android.contacts/data/phones",
                "contact_id:display_name:data1:raw_contact_id",
                cancellationToken)
            .ConfigureAwait(false);

        if (IsPermissionDenied(phones))
        {
            phones = await QueryProviderWithRootFallbackAsync(
                    "content://contacts/phones",
                    "contact_id:display_name:number",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (IsPermissionDenied(phones))
        {
            throw new InvalidOperationException(
                "Cihaz ADB ile rehber okumaya izin vermiyor (READ_CONTACTS). " +
                "Bazı ROM'larda (HyperOS/MIUI) bu engellenir; root veya cihaz ayarlarından izin gerekebilir.");
        }

        if (!LooksLikeFailure(phones))
        {
            foreach (var row in AdbContentQueryParser.ParseRows(phones))
            {
                if (!TryLong(row, "contact_id", out var id) && !TryLong(row, "_id", out id))
                    continue;

                row.TryGetValue("display_name", out var name);
                row.TryGetValue("data1", out var data1);
                row.TryGetValue("number", out var number);
                var phone = FirstNonEmpty(data1, number);
                TryLong(row, "raw_contact_id", out var rawId);

                if (!map.TryGetValue(id, out var acc))
                {
                    acc = new ContactAccumulator(id, rawId > 0 ? rawId : null, name?.Trim() ?? string.Empty);
                    map[id] = acc;
                }
                else if (string.IsNullOrWhiteSpace(acc.DisplayName) && !string.IsNullOrWhiteSpace(name))
                {
                    acc.DisplayName = name.Trim();
                }

                acc.AddPhone(phone);
            }
        }

        var emails = await QueryProviderWithRootFallbackAsync(
                "content://com.android.contacts/data/emails",
                "contact_id:data1",
                cancellationToken)
            .ConfigureAwait(false);

        if (!LooksLikeFailure(emails) && !IsPermissionDenied(emails))
        {
            foreach (var row in AdbContentQueryParser.ParseRows(emails))
            {
                if (!TryLong(row, "contact_id", out var id))
                    continue;
                row.TryGetValue("data1", out var email);
                if (string.IsNullOrWhiteSpace(email))
                    continue;

                if (map.TryGetValue(id, out var acc))
                {
                    if (string.IsNullOrWhiteSpace(acc.Email))
                        acc.Email = email.Trim();
                }
                else
                {
                    map[id] = new ContactAccumulator(id, null, email.Trim())
                    {
                        Email = email.Trim()
                    };
                }
            }
        }

        return map.Values
            .Select(a => a.ToPhoneContact())
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(c => c.PhoneNumber, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<PhoneContact>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var all = await GetContactsAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(query))
            return all;

        var q = query.Trim();
        return all
            .Where(c =>
                c.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.PhoneNumber.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Email.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Organization.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public async Task<PhoneContact> CreateAsync(
        string displayName,
        string phoneNumber,
        string? email = null,
        CancellationToken cancellationToken = default)
    {
        EnsureDevice();
        displayName = displayName.Trim();
        phoneNumber = phoneNumber.Trim();
        email = email?.Trim();

        if (string.IsNullOrWhiteSpace(displayName))
            throw new InvalidOperationException("Kişi adı gerekli.");
        if (string.IsNullOrWhiteSpace(phoneNumber) && string.IsNullOrWhiteSpace(email))
            throw new InvalidOperationException("Telefon veya e-posta gerekli.");

        var insertRaw = await _adb.ExecuteShellAsync(
                "content insert --uri content://com.android.contacts/raw_contacts " +
                "--bind account_type:n: --bind account_name:n:",
                cancellationToken)
            .ConfigureAwait(false);

        var rawId = ParseInsertedId(insertRaw);
        if (rawId <= 0)
        {
            // Bazı cihazlar URI yazdırmaz; raw_contacts son kaydı dene.
            rawId = await PeekLastRawContactIdAsync(cancellationToken).ConfigureAwait(false);
        }

        if (rawId <= 0)
            throw new InvalidOperationException(
                "Kişi oluşturulamadı. Cihaz rehber yazmaya izin vermiyor olabilir.\n" + Snippet(insertRaw));

        await InsertDataAsync(rawId, MimeName, displayName, data2: displayName, cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(phoneNumber))
        {
            await InsertDataAsync(rawId, MimePhone, phoneNumber, data2: "2", cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            await InsertDataAsync(rawId, MimeEmail, email, data2: "1", cancellationToken)
                .ConfigureAwait(false);
        }

        var contacts = await GetContactsAsync(cancellationToken).ConfigureAwait(false);
        var created = contacts.FirstOrDefault(c =>
            string.Equals(c.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(phoneNumber)
                || PhoneNumberNormalizer.TryMatch(c.PhoneNumber, phoneNumber)));

        return created ?? new PhoneContact
        {
            ContactId = 0,
            RawContactId = rawId,
            DisplayName = displayName,
            PhoneNumber = phoneNumber,
            Email = email ?? string.Empty
        };
    }

    public async Task UpdateAsync(
        PhoneContact contact,
        CancellationToken cancellationToken = default)
    {
        EnsureDevice();
        if (contact.ContactId <= 0)
            throw new InvalidOperationException("Geçersiz kişi kimliği.");

        var name = contact.DisplayName.Trim();
        var phone = contact.PhoneNumber.Trim();
        var email = contact.Email.Trim();

        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Kişi adı gerekli.");

        await UpsertDataFieldAsync(contact.ContactId, MimeName, name, cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(phone))
        {
            await UpsertDataFieldAsync(contact.ContactId, MimePhone, phone, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            await UpsertDataFieldAsync(contact.ContactId, MimeEmail, email, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task DeleteAsync(
        long contactId,
        CancellationToken cancellationToken = default)
    {
        EnsureDevice();
        if (contactId <= 0)
            throw new InvalidOperationException("Geçersiz kişi kimliği.");

        var output = await _adb.ExecuteShellAsync(
                $"content delete --uri content://com.android.contacts/contacts/{contactId}",
                cancellationToken)
            .ConfigureAwait(false);

        if (IsPermissionDenied(output) || LooksLikeHardFailure(output))
        {
            output = await _adb.ExecuteShellAsync(
                    "content delete --uri content://com.android.contacts/raw_contacts " +
                    $"--where \"contact_id={contactId}\"",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (IsPermissionDenied(output))
            throw new InvalidOperationException("Kişi silinemedi: izin reddedildi.");
        if (LooksLikeHardFailure(output))
            throw new InvalidOperationException("Kişi silinemedi: " + Snippet(output));
    }

    public async Task<string> ExportToVcfAsync(
        string savePath,
        CancellationToken cancellationToken = default)
    {
        var contacts = await GetContactsAsync(cancellationToken).ConfigureAwait(false);
        var path = PhoneBookExport.ResolvePath(savePath, "contacts", "vcf");
        var sb = new StringBuilder();
        foreach (var c in contacts)
        {
            sb.AppendLine("BEGIN:VCARD");
            sb.AppendLine("VERSION:3.0");
            sb.AppendLine("FN:" + EscapeVcf(c.DisplayName));
            if (!string.IsNullOrWhiteSpace(c.PhoneNumber))
                sb.AppendLine("TEL;TYPE=CELL:" + EscapeVcf(c.PhoneNumber));
            if (!string.IsNullOrWhiteSpace(c.Email))
                sb.AppendLine("EMAIL:" + EscapeVcf(c.Email));
            if (!string.IsNullOrWhiteSpace(c.Organization))
                sb.AppendLine("ORG:" + EscapeVcf(c.Organization));
            sb.AppendLine("END:VCARD");
        }

        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
        return path;
    }

    public async Task<string> ExportToJsonAsync(
        string savePath,
        CancellationToken cancellationToken = default)
    {
        var contacts = await GetContactsAsync(cancellationToken).ConfigureAwait(false);
        var path = PhoneBookExport.ResolvePath(savePath, "contacts", "json");
        var payload = contacts.Select(c => new
        {
            c.ContactId,
            c.DisplayName,
            c.PhoneNumber,
            c.Email,
            c.Organization
        });
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public async Task<string> ExportToCsvAsync(
        string savePath,
        CancellationToken cancellationToken = default)
    {
        var contacts = await GetContactsAsync(cancellationToken).ConfigureAwait(false);
        var path = PhoneBookExport.ResolvePath(savePath, "contacts", "csv");
        var sb = new StringBuilder();
        sb.AppendLine("ContactId,DisplayName,PhoneNumber,Email,Organization");
        foreach (var c in contacts)
        {
            sb.Append(c.ContactId).Append(',')
                .Append(PhoneBookExport.Csv(c.DisplayName)).Append(',')
                .Append(PhoneBookExport.Csv(c.PhoneNumber)).Append(',')
                .Append(PhoneBookExport.Csv(c.Email)).Append(',')
                .Append(PhoneBookExport.Csv(c.Organization))
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
        var contacts = await GetContactsAsync(cancellationToken).ConfigureAwait(false);
        var path = PhoneBookExport.ResolvePath(savePath, "contacts", "xls");
        await PhoneBookExport.WriteExcelAsync(
                path,
                "Kişiler",
                ["ContactId", "Ad", "Telefon", "E-posta", "Kurum"],
                contacts.Select(c => (IReadOnlyList<string>)
                [
                    c.ContactId.ToString(CultureInfo.InvariantCulture),
                    c.DisplayName,
                    c.PhoneNumber,
                    c.Email,
                    c.Organization
                ]),
                cancellationToken)
            .ConfigureAwait(false);
        return path;
    }

    public async Task<int> ImportFromVcfAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        EnsureDevice();
        if (!File.Exists(filePath))
            throw new FileNotFoundException("VCF bulunamadı.", filePath);

        var text = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        var cards = ParseVcf(text);
        var imported = 0;

        foreach (var card in cards)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(card.DisplayName)
                && string.IsNullOrWhiteSpace(card.Phone)
                && string.IsNullOrWhiteSpace(card.Email))
                continue;

            var name = string.IsNullOrWhiteSpace(card.DisplayName)
                ? (card.Phone.Length > 0 ? card.Phone : card.Email)
                : card.DisplayName;

            try
            {
                await CreateAsync(name, card.Phone, card.Email, cancellationToken).ConfigureAwait(false);
                imported++;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "VCF kişi atlandı: {Name}", name);
            }
        }

        return imported;
    }

    private async Task UpsertDataFieldAsync(
        long contactId,
        string mimeType,
        string value,
        CancellationToken cancellationToken)
    {
        var raw = await _adb.ExecuteShellAsync(
                "content query --uri content://com.android.contacts/data " +
                $"--projection _id:raw_contact_id:data1 " +
                $"--where \"contact_id={contactId} AND mimetype='{mimeType}'\"",
                cancellationToken)
            .ConfigureAwait(false);

        var rows = AdbContentQueryParser.ParseRows(raw);
        if (rows.Count > 0 && TryLong(rows[0], "_id", out var dataId) && dataId > 0)
        {
            var update = await _adb.ExecuteShellAsync(
                    "content update --uri content://com.android.contacts/data " +
                    $"--bind data1:s:{ShellBind(value)} --where \"_id={dataId}\"",
                    cancellationToken)
                .ConfigureAwait(false);
            if (IsPermissionDenied(update))
                throw new InvalidOperationException("Kişi güncellenemedi: izin reddedildi.");
            return;
        }

        long rawId = 0;
        if (rows.Count > 0)
            TryLong(rows[0], "raw_contact_id", out rawId);

        if (rawId <= 0)
        {
            var rawLookup = await _adb.ExecuteShellAsync(
                    "content query --uri content://com.android.contacts/raw_contacts " +
                    $"--projection _id --where \"contact_id={contactId}\"",
                    cancellationToken)
                .ConfigureAwait(false);
            var rawRows = AdbContentQueryParser.ParseRows(rawLookup);
            if (rawRows.Count > 0)
                TryLong(rawRows[0], "_id", out rawId);
        }

        if (rawId <= 0)
            throw new InvalidOperationException("Kişinin raw_contact_id bulunamadı; güncelleme yapılamadı.");

        await InsertDataAsync(rawId, mimeType, value, data2: mimeType == MimePhone ? "2" : null, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task InsertDataAsync(
        long rawContactId,
        string mimeType,
        string data1,
        string? data2,
        CancellationToken cancellationToken)
    {
        var cmd = new StringBuilder();
        cmd.Append("content insert --uri content://com.android.contacts/data ");
        cmd.Append($"--bind raw_contact_id:i:{rawContactId} ");
        cmd.Append($"--bind mimetype:s:{ShellBind(mimeType)} ");
        cmd.Append($"--bind data1:s:{ShellBind(data1)}");
        if (!string.IsNullOrEmpty(data2))
            cmd.Append($" --bind data2:s:{ShellBind(data2)}");

        var output = await _adb.ExecuteShellAsync(cmd.ToString(), cancellationToken).ConfigureAwait(false);
        if (IsPermissionDenied(output))
            throw new InvalidOperationException("Rehber yazma izni yok.");
        if (LooksLikeHardFailure(output))
            _logger.Warning("Contact data insert uyarısı: {Out}", Snippet(output));
    }

    private async Task<long> PeekLastRawContactIdAsync(CancellationToken cancellationToken)
    {
        var raw = await _adb.ExecuteShellAsync(
                "content query --uri content://com.android.contacts/raw_contacts " +
                "--projection _id --sort \"_id DESC\"",
                cancellationToken)
            .ConfigureAwait(false);
        var first = AdbContentQueryParser.ParseRows(raw).FirstOrDefault();
        return first is not null && TryLong(first, "_id", out var id) ? id : 0;
    }

    private async Task<string> QueryProviderWithRootFallbackAsync(
        string uri,
        string projection,
        CancellationToken cancellationToken)
    {
        var raw = await QueryProviderAsync(uri, projection, cancellationToken).ConfigureAwait(false);
        if (!LooksLikeFailure(raw) && !IsPermissionDenied(raw) && HasContentRows(raw))
            return raw;

        if (IsPermissionDenied(raw) || LooksLikeFailure(raw))
        {
            var rootRaw = await QueryProviderAsRootAsync(uri, projection, cancellationToken)
                .ConfigureAwait(false);
            if (!LooksLikeFailure(rootRaw) && !IsPermissionDenied(rootRaw))
            {
                _logger.Information("Rehber root üzerinden okundu ({Uri})", uri);
                return rootRaw;
            }
        }

        return raw;
    }

    private async Task<string> QueryProviderAsRootAsync(
        string uri,
        string projection,
        CancellationToken cancellationToken)
    {
        var cmd = $"content query --uri {uri} --projection {projection}";
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
            _logger.Debug(ex, "su -c contacts query failed");
        }

        try
        {
            return await _adb.ExecuteShellAsync($"su 0 -c '{escaped}'", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "su 0 -c contacts query failed");
            return string.Empty;
        }
    }

    private async Task<string> QueryProviderAsync(
        string uri,
        string projection,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _adb.ExecuteShellAsync(
                    $"content query --uri {uri} --projection {projection}",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Contacts query failed: {Uri}", uri);
            return string.Empty;
        }
    }

    private void EnsureDevice()
    {
        if (_adb.SelectedDevice is null)
            throw new InvalidOperationException("Rehber için bağlı bir cihaz seçin.");
    }

    private static long ParseInsertedId(string output)
    {
        var m = Regex.Match(output, @"/(\d+)\s*$", RegexOptions.Multiline);
        if (m.Success && long.TryParse(m.Groups[1].Value, out var id))
            return id;
        m = Regex.Match(output, @"Row:.*?_id=(\d+)", RegexOptions.IgnoreCase);
        return m.Success && long.TryParse(m.Groups[1].Value, out id) ? id : 0;
    }

    private static bool TryLong(Dictionary<string, string> row, string key, out long value)
    {
        value = 0;
        return row.TryGetValue(key, out var s)
               && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }

        return string.Empty;
    }

    private static bool HasContentRows(string output) =>
        output.Contains("Row:", StringComparison.OrdinalIgnoreCase);

    private static bool IsSuDenied(string output)
    {
        var o = output.Trim();
        return o.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
               || o.Contains("not allowed", StringComparison.OrdinalIgnoreCase)
               || o.Contains("not found", StringComparison.OrdinalIgnoreCase)
               || o.Contains("Can't find", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePhone(string phone) => PhoneNumberNormalizer.Normalize(phone);

    private static string ShellBind(string value) =>
        value.Replace("'", "'\\''", StringComparison.Ordinal);

    private static string EscapeVcf(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static bool IsPermissionDenied(string output) =>
        output.Contains("Permission Denial", StringComparison.OrdinalIgnoreCase)
        || output.Contains("SecurityException", StringComparison.OrdinalIgnoreCase)
        || output.Contains("READ_CONTACTS", StringComparison.OrdinalIgnoreCase)
        || output.Contains("WRITE_CONTACTS", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeFailure(string output) =>
        string.IsNullOrWhiteSpace(output)
        || IsPermissionDenied(output)
        || (output.Contains("Exception", StringComparison.OrdinalIgnoreCase)
            && !output.Contains("Row:", StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeHardFailure(string output) =>
        IsPermissionDenied(output)
        || output.Contains("Error", StringComparison.OrdinalIgnoreCase)
        || output.Contains("Exception", StringComparison.OrdinalIgnoreCase);

    private static string Snippet(string output)
    {
        var line = output.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 180 ? line : line[..180] + "…";
    }

    private static List<VcfCard> ParseVcf(string text)
    {
        var cards = new List<VcfCard>();
        VcfCard? current = null;
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Equals("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                current = new VcfCard();
                continue;
            }

            if (line.Equals("END:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null)
                    cards.Add(current);
                current = null;
                continue;
            }

            if (current is null)
                continue;

            var idx = line.IndexOf(':');
            if (idx <= 0)
                continue;

            var key = line[..idx];
            var val = UnescapeVcf(line[(idx + 1)..].Trim());
            if (key.StartsWith("FN", StringComparison.OrdinalIgnoreCase))
                current.DisplayName = val;
            else if (key.StartsWith("TEL", StringComparison.OrdinalIgnoreCase) && current.Phone.Length == 0)
                current.Phone = val;
            else if (key.StartsWith("EMAIL", StringComparison.OrdinalIgnoreCase) && current.Email.Length == 0)
                current.Email = val;
            else if (key.StartsWith("ORG", StringComparison.OrdinalIgnoreCase))
                current.Organization = val;
        }

        return cards;
    }

    private static string UnescapeVcf(string value) =>
        value.Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\,", ",", StringComparison.Ordinal)
            .Replace("\\;", ";", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);

    private sealed class ContactAccumulator
    {
        private readonly List<string> _phones = [];

        public ContactAccumulator(long contactId, long? rawContactId, string displayName)
        {
            ContactId = contactId;
            RawContactId = rawContactId;
            DisplayName = displayName;
        }

        public long ContactId { get; }
        public long? RawContactId { get; init; }
        public string DisplayName { get; set; }
        public string Email { get; set; } = string.Empty;

        public void AddPhone(string phone)
        {
            if (string.IsNullOrWhiteSpace(phone))
                return;

            var p = phone.Trim();
            if (_phones.Any(existing => PhoneNumberNormalizer.TryMatch(existing, p)))
                return;

            _phones.Add(p);
        }

        public PhoneContact ToPhoneContact() => new()
        {
            ContactId = ContactId,
            RawContactId = RawContactId,
            DisplayName = DisplayName,
            PhoneNumber = _phones.FirstOrDefault() ?? string.Empty,
            Email = Email,
            AllPhoneNumbers = _phones.ToArray()
        };
    }

    private sealed class VcfCard
    {
        public string DisplayName { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Organization { get; set; } = string.Empty;
    }
}
