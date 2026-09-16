using AndroidManager.Core.Models;

namespace AndroidManager.Security.Engines;

/// <summary>
/// Aggregates evidence weights into risk/confidence scores to reduce false positives.
/// </summary>
public static class RiskEngine
{
    public const int ThreatThreshold = 25;
    public const int CriticalThreshold = 70;

    public static ThreatItem Finalize(ThreatItem draft)
    {
        var score = draft.Evidence.Sum(e => e.ScoreWeight);
        if (score == 0 && draft.RiskScore > 0)
            score = draft.RiskScore;

        draft.RiskScore = Math.Clamp(score, 0, 200);
        draft.ConfidenceScore = Math.Clamp(
            draft.Evidence.Count * 18 + Math.Min(40, draft.RiskScore / 2),
            10,
            100);

        if (draft.Severity == default || draft.Severity == ThreatSeverity.Info)
        {
            draft.Severity = draft.RiskScore switch
            {
                >= CriticalThreshold => ThreatSeverity.Critical,
                >= 45 => ThreatSeverity.High,
                >= ThreatThreshold => ThreatSeverity.Medium,
                >= 10 => ThreatSeverity.Low,
                _ => ThreatSeverity.Info
            };
        }

        return draft;
    }

    public static bool MeetsThreatThreshold(ThreatItem item) =>
        item.Type is ThreatType.Malware or ThreatType.Trojan or ThreatType.Rootkit or ThreatType.Spyware
            or ThreatType.Ransomware
        || item.RiskScore >= ThreatThreshold
        || item.Severity >= ThreatSeverity.High;

    public static SecurityScoreCard BuildScoreCard(
        IReadOnlyList<ThreatItem> threats,
        IReadOnlyList<ThreatItem> riskFactors)
    {
        var score = 100;
        var warnings = new List<string>();
        var positives = new List<string>();

        foreach (var t in threats)
        {
            score -= t.Severity switch
            {
                ThreatSeverity.Critical => 25,
                ThreatSeverity.High => 15,
                ThreatSeverity.Medium => 8,
                ThreatSeverity.Low => 3,
                _ => 1
            };
            warnings.Add($"{t.SeverityLabel}: {t.Name}");
        }

        foreach (var r in riskFactors)
        {
            score -= r.Severity switch
            {
                ThreatSeverity.Critical => 12,
                ThreatSeverity.High => 8,
                ThreatSeverity.Medium => 4,
                _ => 2
            };
            warnings.Add($"Risk: {r.Name}");
        }

        score = Math.Clamp(score, 0, 100);

        if (threats.Count == 0)
            positives.Add("Bilinen malware imzası bulunamadı");
        if (riskFactors.All(r => !r.Name.Contains("Verified Boot", StringComparison.OrdinalIgnoreCase)))
            positives.Add("Verified Boot durumu kontrol edildi");

        var risk = score switch
        {
            >= 85 => SecurityRiskLevel.Low,
            >= 65 => SecurityRiskLevel.Medium,
            >= 40 => SecurityRiskLevel.High,
            _ => SecurityRiskLevel.Critical
        };

        var avgConfidence = threats.Count == 0
            ? 70
            : (int)threats.Average(t => t.ConfidenceScore);

        return new SecurityScoreCard
        {
            Score = score,
            RiskLevel = risk,
            ConfidenceLabel = avgConfidence >= 70 ? "Yüksek" : avgConfidence >= 40 ? "Orta" : "Düşük",
            Positives = positives.Take(5).ToList(),
            Warnings = warnings.Take(8).ToList()
        };
    }
}
