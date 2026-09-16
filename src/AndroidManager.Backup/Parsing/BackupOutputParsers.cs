using System.Globalization;
using System.Text.RegularExpressions;
using AndroidManager.Core.Models;

namespace AndroidManager.Backup.Parsing;

public static partial class BackupOutputParsers
{
    public static IReadOnlyList<SmsMessage> ParseSmsOutput(string raw)
    {
        var messages = new List<SmsMessage>();
        foreach (Match m in SmsRowRegex().Matches(raw))
        {
            messages.Add(new SmsMessage
            {
                Id = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                Address = m.Groups[2].Value.Trim(),
                Body = m.Groups[3].Value.Trim(),
                Date = long.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
                Type = int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture)
            });
        }

        return messages.OrderByDescending(m => m.Date).ToList();
    }

    public static IReadOnlyList<Contact> ParseContacts(string raw)
    {
        var contacts = new List<Contact>();
        foreach (Match m in ContactRowRegex().Matches(raw))
        {
            contacts.Add(new Contact
            {
                Id = long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                    ? id
                    : 0,
                DisplayName = m.Groups[2].Value.Trim(),
                PhoneNumber = m.Groups[3].Value.Trim()
            });
        }

        return contacts
            .GroupBy(c => c.PhoneNumber)
            .Select(g => g.First())
            .OrderBy(c => c.DisplayName)
            .ToList();
    }

    [GeneratedRegex(@"_id=(\d+).*?address=([^,\n]+).*?body=(.+?), date=(\d+), type=(\d+)", RegexOptions.Singleline)]
    private static partial Regex SmsRowRegex();

    [GeneratedRegex(@"(?:contact_id|_id)=(\d+).*?(?:display_name)=([^,\n]+).*?(?:data1|number)=([^\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ContactRowRegex();
}
