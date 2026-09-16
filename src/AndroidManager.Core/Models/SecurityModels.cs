namespace AndroidManager.Core.Models;

public enum ThreatType
{
    Malware,
    Adware,
    Spyware,
    Ransomware,
    Trojan,
    Rootkit,
    Pua,
    Suspicious,
    RiskFactor,
    Clean
}

public enum ThreatSeverity
{
    Info = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}

public enum ThreatCategory
{
    App,
    File,
    Network,
    System,
    Permission,
    Persistence,
    Privilege,
    Integrity
}

public enum ScanStatus
{
    Running,
    Completed,
    Cancelled,
    Failed
}

public enum CleanAction
{
    Uninstalled,
    FileDeleted,
    Quarantined,
    Disabled,
    ManualRequired
}

public enum SecurityRiskLevel
{
    Low,
    Medium,
    High,
    Critical
}

public sealed class ThreatEvidence
{
    public string Code { get; init; } = "";
    public string Description { get; init; } = "";
    public int ScoreWeight { get; init; }
}

public sealed class ThreatItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string PackageName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public ThreatType Type { get; set; }
    public ThreatSeverity Severity { get; set; }
    public ThreatCategory Category { get; set; }
    public string Description { get; set; } = "";
    public string HashMd5 { get; set; } = "";
    public string HashSha256 { get; set; } = "";
    public string CertificateSha256 { get; set; } = "";
    public string VersionName { get; set; } = "";
    public DateTime DetectedAt { get; set; } = DateTime.Now;
    public bool IsSystem { get; set; }
    public bool IsQuarantined { get; set; }
    public int RiskScore { get; set; }
    public int ConfidenceScore { get; set; }
    public string QuarantineLocalPath { get; set; } = "";
    public List<string> SuspiciousPermissions { get; set; } = [];
    public List<string> SuspiciousActivities { get; set; } = [];
    public List<string> NetworkIndicators { get; set; } = [];
    public List<ThreatEvidence> Evidence { get; set; } = [];
    public string RemediationAdvice { get; set; } = "";

    public string SeverityLabel => Severity switch
    {
        ThreatSeverity.Critical => "Kritik",
        ThreatSeverity.High => "Yüksek",
        ThreatSeverity.Medium => "Orta",
        ThreatSeverity.Low => "Düşük",
        _ => "Bilgi"
    };

    public string SeverityColor => Severity switch
    {
        ThreatSeverity.Critical => "#F44336",
        ThreatSeverity.High => "#FF5722",
        ThreatSeverity.Medium => "#FFC107",
        ThreatSeverity.Low => "#8BC34A",
        _ => "#9E9E9E"
    };

    public string TypeLabel => Type switch
    {
        ThreatType.Malware => "Malware",
        ThreatType.Adware => "Adware",
        ThreatType.Spyware => "Spyware",
        ThreatType.Ransomware => "Ransomware",
        ThreatType.Trojan => "Trojan",
        ThreatType.Rootkit => "Rootkit",
        ThreatType.Pua => "İstenmeyen uygulama",
        ThreatType.Suspicious => "Şüpheli",
        ThreatType.RiskFactor => "Risk faktörü",
        _ => "Bilinmiyor"
    };
}

public sealed class ScanOptions
{
    public bool ScanApps { get; init; } = true;
    public bool ScanFiles { get; init; } = true;
    public bool ScanPermissions { get; init; } = true;
    public bool ScanNetwork { get; init; } = true;
    public bool ScanSystemFiles { get; init; }
    public bool ScanPrivileges { get; init; } = true;
    public bool ScanPersistence { get; init; } = true;
    public bool ScanIntegrity { get; init; } = true;
    public bool DeepScan { get; init; }
    public bool RescueMode { get; init; }
    public IReadOnlyList<string> ExcludedPaths { get; init; } = [];
}

public sealed class ScanProgress
{
    public string CurrentItem { get; init; } = "";
    public string Phase { get; init; } = "";
    public int Percentage { get; init; }
    public int ScannedCount { get; init; }
    public int TotalCount { get; init; }
    public int ThreatsFound { get; init; }
    public string ElapsedTime { get; init; } = "";
}

public sealed class ScanSession
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime StartedAt { get; init; } = DateTime.Now;
    public DateTime CompletedAt { get; set; }
    public TimeSpan Duration => CompletedAt == default ? TimeSpan.Zero : CompletedAt - StartedAt;
    public List<ThreatItem> Threats { get; init; } = [];
    public List<ThreatItem> RiskFactors { get; init; } = [];
    public int ScannedApps { get; set; }
    public int ScannedFiles { get; set; }
    public ScanStatus Status { get; set; }
    public string DeviceModel { get; init; } = "";
    public SecurityScoreCard? Score { get; set; }

    public int CriticalCount => Threats.Count(t => t.Severity == ThreatSeverity.Critical);
    public int HighCount => Threats.Count(t => t.Severity == ThreatSeverity.High);
    public bool IsClean => Threats.Count == 0;
}

public sealed class CleanResult
{
    public ThreatItem Threat { get; init; } = null!;
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public CleanAction ActionTaken { get; init; }
}

public sealed class CleanProgress
{
    public string CurrentThreat { get; init; } = "";
    public int Percentage { get; init; }
    public int Done { get; init; }
    public int Total { get; init; }
}

public sealed class SecurityStatus
{
    public DateTime LastScanDate { get; init; }
    public bool IsClean { get; init; } = true;
    public int ThreatCount { get; init; }
    public bool DefinitionsUpToDate { get; init; }
    public DateTime DefinitionsDate { get; init; }
    public string DefinitionsVersion { get; init; } = "";
    public int QuarantineCount { get; init; }
    public SecurityScoreCard? Score { get; init; }

    public string StatusText =>
        LastScanDate == default
            ? "Henüz tarama yapılmadı"
            : IsClean
                ? "Bilinen tehdit bulunamadı"
                : $"{ThreatCount} tehdit bulundu";

    public string StatusColor =>
        LastScanDate == default ? "#455A64" : IsClean ? "#4CAF50" : "#F44336";
}

public sealed class SecurityScoreCard
{
    public int Score { get; init; } = 100;
    public SecurityRiskLevel RiskLevel { get; init; } = SecurityRiskLevel.Low;
    public string ConfidenceLabel { get; init; } = "Orta";
    public List<string> Positives { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed class ScanHistoryEntry
{
    public string SessionId { get; init; } = "";
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public int ThreatCount { get; init; }
    public int CriticalCount { get; init; }
    public bool IsClean { get; init; }
    public int Score { get; init; }
    public string DeviceModel { get; init; } = "";
}
