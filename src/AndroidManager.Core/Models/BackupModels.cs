namespace AndroidManager.Core.Models;

public sealed class BackupJob
{
    public int Id { get; set; }
    public string DeviceModel { get; set; } = string.Empty;
    public string DeviceSerial { get; set; } = string.Empty;
    public BackupType Type { get; set; }
    public string SavePath { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public long SizeBytes { get; set; }
    public BackupStatus Status { get; set; }
    public string Note { get; set; } = string.Empty;

    public string SizeFormatted => SizeBytes switch
    {
        < 1_048_576 => $"{SizeBytes / 1024.0:F1} KB",
        < 1_073_741_824 => $"{SizeBytes / 1_048_576.0:F1} MB",
        _ => $"{SizeBytes / 1_073_741_824.0:F2} GB"
    };
}

[Flags]
public enum BackupType
{
    None = 0,
    Sms = 1 << 0,
    Contacts = 1 << 1,
    Photos = 1 << 2,
    Videos = 1 << 3,
    Apps = 1 << 4,
    /// <summary>/data/data + Android/data + OBB (root gerekir — oyun kayıtları).</summary>
    AppData = 1 << 5,
    All = Sms | Contacts | Photos | Videos | Apps | AppData
}

public enum BackupStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

public sealed class BackupOptions
{
    public BackupType Type { get; init; } = BackupType.All;
    public string SavePath { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
    public bool Compress { get; init; } = true;
}

public sealed class RestoreOptions
{
    public BackupType Type { get; init; } = BackupType.All;
    public bool OverwriteApps { get; init; }

    /// <summary>
    /// Null: yedekteki tüm uygulamalar. Boş liste: uygulama/APK/veri yok.
    /// </summary>
    public IReadOnlyList<string>? PackageNames { get; init; }
}

public sealed class BackupAppEntry
{
    public string PackageName { get; init; } = string.Empty;
    public bool HasApk { get; init; }
    public bool HasData { get; init; }
    public bool IsLikelySystem { get; init; }
    public long ApkBytes { get; init; }
    public long DataBytes { get; init; }

    public long TotalBytes => ApkBytes + DataBytes;

    public string SizeFormatted => SizeFormatter.Format(TotalBytes);
}

public sealed class BackupProgress
{
    public string CurrentStep { get; init; } = string.Empty;
    public BackupType CurrentType { get; init; }
    public int Percentage { get; init; }
    public int ItemsDone { get; init; }
    public int ItemsTotal { get; init; }
    public string Speed { get; init; } = string.Empty;
}

public sealed class RestoreResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public int ItemRestored { get; init; }
}

public sealed class SmsMessage
{
    public long Id { get; init; }
    public string Address { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public long Date { get; init; }
    public int Type { get; init; }
    public bool IsRead { get; init; } = true;
    public int ThreadId { get; init; }

    public string TypeLabel => Type switch
    {
        1 => "Gelen",
        2 => "Giden",
        3 => "Taslak",
        _ => "Diğer"
    };

    public bool IsIncoming => Type == 1;

    public DateTime DateLocal => DateTimeOffset.FromUnixTimeMilliseconds(
            Date > 0 ? Date : 0)
        .LocalDateTime;
}

public sealed class SmsThread
{
    public string Address { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string ContactName { get; init; } = string.Empty;
    public string LastBody { get; init; } = string.Empty;
    public long LastDate { get; init; }
    public int MessageCount { get; init; }
    public int UnreadCount { get; init; }

    public DateTime LastDateLocal => DateTimeOffset.FromUnixTimeMilliseconds(
            LastDate > 0 ? LastDate : 0)
        .LocalDateTime;

    public string EffectiveDisplayName =>
        !string.IsNullOrWhiteSpace(ContactName) ? ContactName
        : !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName
        : Address;

    public string LastDateFormatted => LastDateLocal.Date == DateTime.Today
        ? LastDateLocal.ToString("HH:mm")
        : LastDateLocal.ToString("dd.MM.yyyy");

    public bool ShowAddressSubtitle =>
        !string.IsNullOrWhiteSpace(ContactName)
        && !string.Equals(ContactName.Trim(), Address.Trim(), StringComparison.OrdinalIgnoreCase);

    public string Initial
    {
        get
        {
            var name = EffectiveDisplayName;
            return name.Length > 0
                ? name[0].ToString().ToUpperInvariant()
                : "?";
        }
    }
}

public sealed class SmsSendResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool RequiresUserConfirm { get; init; }
}

public sealed class Contact
{
    public long Id { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string PhoneNumber { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string VCard { get; init; } = string.Empty;
}

public sealed class MediaFile
{
    public string RemotePath { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public string MimeType { get; init; } = string.Empty;
}
