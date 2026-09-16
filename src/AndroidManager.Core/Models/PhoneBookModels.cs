namespace AndroidManager.Core.Models;

/// <summary>Cihaz rehberindeki kişi (ADB Contacts Provider).</summary>
public sealed class PhoneContact
{
    public long ContactId { get; init; }
    public long? RawContactId { get; init; }
    public string DisplayName { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public IReadOnlyList<string> AllPhoneNumbers { get; init; } = [];
    public string Email { get; set; } = string.Empty;
    public string Organization { get; set; } = string.Empty;

    public string PrimaryLine =>
        string.IsNullOrWhiteSpace(PhoneNumber) ? Email : PhoneNumber;

    public string Initials
    {
        get
        {
            var name = DisplayName.Trim();
            if (name.Length == 0)
                return "?";
            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return string.Concat(parts[0][0], parts[^1][0]).ToUpperInvariant();
            return name[..1].ToUpperInvariant();
        }
    }
}

public enum CallDirection
{
    Unknown = 0,
    Incoming = 1,
    Outgoing = 2,
    Missed = 3,
    Voicemail = 4,
    Rejected = 5,
    Blocked = 6,
    AnsweredExternally = 7
}

public sealed class CallLogEntry
{
    public long Id { get; init; }
    public string Number { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public long DateMs { get; init; }
    public int DurationSeconds { get; init; }
    public int Type { get; init; }

    public CallDirection Direction =>
        Enum.IsDefined(typeof(CallDirection), Type)
            ? (CallDirection)Type
            : CallDirection.Unknown;

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name) ? Number : Name;

    public DateTime DateLocal =>
        DateTimeOffset.FromUnixTimeMilliseconds(DateMs).LocalDateTime;

    public string TimeFormatted => DateLocal.ToString("HH:mm");

    public string DateFormatted => DateLocal.ToString("dd.MM.yyyy");

    public string DurationFormatted
    {
        get
        {
            if (DurationSeconds <= 0)
                return Direction is CallDirection.Missed or CallDirection.Rejected
                    ? "—"
                    : "00:00";
            var ts = TimeSpan.FromSeconds(DurationSeconds);
            return ts.TotalHours >= 1
                ? ts.ToString(@"h\:mm\:ss")
                : ts.ToString(@"mm\:ss");
        }
    }

    public string TypeLabel => Direction switch
    {
        CallDirection.Incoming => "Gelen",
        CallDirection.Outgoing => "Giden",
        CallDirection.Missed => "Cevapsız",
        CallDirection.Voicemail => "Sesli mesaj",
        CallDirection.Rejected => "Reddedildi",
        CallDirection.Blocked => "Engelli",
        CallDirection.AnsweredExternally => "Harici",
        _ => "Bilinmiyor"
    };

    public string DirectionGlyph => Direction switch
    {
        CallDirection.Incoming => "↓",
        CallDirection.Outgoing => "↑",
        CallDirection.Missed => "✕",
        CallDirection.Rejected => "⊘",
        CallDirection.Blocked => "⛔",
        _ => "•"
    };
}
