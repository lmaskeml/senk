namespace AndroidManager.Core.Models;

public enum DebloatRisk
{
    /// <summary>Güvenle kaldırılabilir.</summary>
    Safe,

    /// <summary>Dikkatli olunmalı.</summary>
    Caution,

    /// <summary>Sistem işlevi bozulabilir.</summary>
    Danger
}

public sealed class DebloatCategory
{
    public string Name { get; init; } = string.Empty;
    public string Icon { get; init; } = "📦";
    public string Color { get; init; } = "#607D8B";
}

public sealed class DebloatCandidate
{
    public string PackageName { get; init; } = string.Empty;
    public string AppName { get; init; } = string.Empty;
    public string Category { get; init; } = "Diğer";
    public string Reason { get; init; } = string.Empty;
    public bool IsKnownBloat { get; init; }
    public bool IsDisabledForUser { get; init; }
    public DebloatRisk Risk { get; init; } = DebloatRisk.Caution;
    public bool IsSystemApp { get; init; } = true;

    public string RiskIcon => Risk switch
    {
        DebloatRisk.Safe => "✅",
        DebloatRisk.Caution => "⚠",
        DebloatRisk.Danger => "❌",
        _ => "?"
    };

    public string RiskLabel => Risk switch
    {
        DebloatRisk.Safe => "Güvenli",
        DebloatRisk.Caution => "Dikkat",
        DebloatRisk.Danger => "Tehlikeli",
        _ => "?"
    };

    public string StatusLabel => IsDisabledForUser ? "Pasif / kaldırıldı" : "Aktif";
}

public sealed class DebloatResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string PackageName { get; init; } = string.Empty;
}

public sealed class DeviceDiagnostics
{
    public BatteryInfo Battery { get; init; } = new();
    public StorageInfo Storage { get; init; } = new();
    public RamInfo Ram { get; init; } = new();
    public CpuInfo Cpu { get; init; } = new();
    public int ChargeCounter { get; init; }
    public int CycleCount { get; init; }
    public string Technology { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public IReadOnlyList<string> Sensors { get; init; } = [];
    public string RawBatteryDump { get; init; } = string.Empty;
    public DateTime CapturedAt { get; init; } = DateTime.Now;

    public string SummaryText
    {
        get
        {
            var sensors = Sensors.Count == 0 ? "—" : string.Join(", ", Sensors.Take(12));
            if (Sensors.Count > 12)
                sensors += $" (+{Sensors.Count - 12})";

            return
                $"SeND ANDROID MANAGER — Cihaz Tanı Raporu\n" +
                $"Zaman: {CapturedAt:yyyy-MM-dd HH:mm:ss}\n\n" +
                $"Pil: {Battery.Level}% | {Battery.Temperature:0.0} °C | Sağlık: {Battery.Health}\n" +
                $"Şarj: {(Battery.IsCharging ? "Evet" : "Hayır")} | Durum: {Status}\n" +
                $"Teknoloji: {Technology} | Döngü: {(CycleCount > 0 ? CycleCount.ToString() : "—")}\n" +
                $"Depolama: {Storage.UsedFormatted} / {Storage.TotalFormatted} (boş {Storage.FreeFormatted})\n" +
                $"RAM: {Ram.UsedFormatted} / {Ram.TotalFormatted}\n" +
                $"CPU: {Cpu.UsagePercent:0.0}%\n" +
                $"Sensörler: {sensors}\n";
        }
    }
}

public sealed class UsbWifiSwitchResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public int Port { get; init; } = 5555;
}
