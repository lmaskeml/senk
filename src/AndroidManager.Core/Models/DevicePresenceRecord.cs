namespace AndroidManager.Core.Models;

/// <summary>Last-known presence for wireless guardian UI (E3 / offline states).</summary>
public sealed class DevicePresenceRecord
{
    public string StableId { get; init; } = "";
    public DateTime LastSeen { get; set; } = DateTime.MinValue;
    public string? LastKnownIp { get; set; }
    public int? LastKnownPort { get; set; }
    public bool LastCompanionOnline { get; set; }
    public string? LastNetworkType { get; set; }
    public int LastNetworkGeneration { get; set; }
    public string? LastPortSource { get; set; }

    public bool IsStale(TimeSpan threshold) =>
        LastSeen == DateTime.MinValue || DateTime.Now - LastSeen > threshold;

    public string LastSeenDisplay
    {
        get
        {
            if (LastSeen == DateTime.MinValue)
                return "bilinmiyor";

            var span = DateTime.Now - LastSeen;
            if (span.TotalMinutes < 1)
                return "az önce";
            if (span.TotalMinutes < 60)
                return $"{(int)span.TotalMinutes} dakika önce";
            if (span.TotalHours < 24)
                return $"{(int)span.TotalHours} saat önce";
            return $"{(int)span.TotalDays} gün önce";
        }
    }
}
