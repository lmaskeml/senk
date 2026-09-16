namespace AndroidManager.Core.Models;

public enum VtVerdict
{
    Clean,
    Suspicious,
    Malicious,
    Unknown,
    Error
}

public sealed class VtConfig
{
    public string ApiKey { get; set; } = "";

    /// <summary>Anahtar olmadan akışı test etmek için deterministik sahte rapor üretir.</summary>
    public bool UseSimulation { get; set; }

    /// <summary>Kötü amaçlı bulunanları tehdit olarak işaretle (otomatik).</summary>
    public bool AutoUpgradeThreats { get; set; } = true;

    /// <summary>İstekler arası minimum bekleme (ücretsiz: 15 sn → 4 istek/dk).</summary>
    public int MinRequestIntervalMs { get; set; } = 15_000;

    /// <summary>Günlük kota (ücretsiz: 500; güvenlik payıyla 450).</summary>
    public int DailyQuota { get; set; } = 450;

    /// <summary>Bu eşiğin üzerinde "malicious" motor varsa tehdit say.</summary>
    public int MaliciousThreshold { get; set; } = 5;

    /// <summary>Analiz tamamlanana kadar bekleme (yükleme sonrası polling).</summary>
    public int UploadPollSeconds { get; set; } = 15;
}

public sealed class VtEngineResult
{
    public string Engine { get; init; } = "";
    public string Category { get; init; } = "";
    public string Result { get; init; } = "";
}

public sealed class VtReport
{
    public string RequestedHash { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string Sha1 { get; init; } = "";
    public string Md5 { get; init; } = "";
    public string MeaningfulName { get; init; } = "";
    public int Malicious { get; init; }
    public int Suspicious { get; init; }
    public int Harmless { get; init; }
    public int Undetected { get; init; }
    public List<VtEngineResult> TopMatches { get; init; } = [];
    public string SuggestedLabel { get; init; } = "";
    public DateTime? LastAnalysisDate { get; init; }
    public DateTime? LastSubmissionDate { get; init; }
    public VtVerdict Verdict { get; init; }

    public int TotalEngines => Malicious + Suspicious + Harmless + Undetected;

    public double Score =>
        TotalEngines == 0 ? 0 : Math.Round(Malicious * 100.0 / TotalEngines, 1);

    public string Permalink =>
        string.IsNullOrEmpty(Sha256) ? "" : $"https://www.virustotal.com/gui/file/{Sha256}";

    public string VerdictText => Verdict switch
    {
        VtVerdict.Malicious => "Kötü Amaçlı",
        VtVerdict.Suspicious => "Şüpheli",
        VtVerdict.Clean => "Temiz",
        VtVerdict.Unknown => "Bilinmiyor",
        _ => "Hata"
    };

    public string VerdictColor => Verdict switch
    {
        VtVerdict.Malicious => "#F44336",
        VtVerdict.Suspicious => "#FF9800",
        VtVerdict.Clean => "#4CAF50",
        _ => "#9E9E9E"
    };

    public string SeverityText => Malicious switch
    {
        >= 10 => "Kritik",
        >= 5 => "Yüksek",
        > 0 => "Orta",
        _ => ""
    };
}

public sealed class VtStatus
{
    public bool HasApiKey { get; init; }
    public bool Simulation { get; init; }
    public int RequestsToday { get; init; }
    public int DailyQuota { get; init; }
    public DateTime NextAllowedAt { get; init; }
    public int CacheEntries { get; init; }

    public int QuotaLeft => Math.Max(0, DailyQuota - RequestsToday);

    public string QuotaText =>
        $"{RequestsToday}/{DailyQuota} istek bugün • {QuotaLeft} kaldı";

    public string NextAllowedText =>
        NextAllowedAt <= DateTime.Now ? "Şimdi" : NextAllowedAt.ToString("HH:mm:ss");
}

public sealed class VtProgress
{
    public string CurrentItem { get; init; } = "";
    public int Done { get; init; }
    public int Total { get; init; }
    public int MaliciousFound { get; init; }
    public string Elapsed { get; init; } = "";
}
