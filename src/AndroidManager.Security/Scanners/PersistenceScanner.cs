using System.Text.RegularExpressions;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Security.Engines;
using Serilog;

namespace AndroidManager.Security.Scanners;

public sealed class PersistenceScanner
{
    private readonly IAdbService _adb;

    public PersistenceScanner(IAdbService adb) => _adb = adb;

    public async Task<List<ThreatItem>> ScanAsync(
        IReadOnlyCollection<string> focusPackages,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var threats = new List<ThreatItem>();
        progress?.Report(new ScanProgress { Phase = "Kalıcılık taranıyor", Percentage = 20 });

        try
        {
            var receivers = await _adb.ExecuteShellAsync(
                "dumpsys package | grep -A2 'android.intent.action.BOOT_COMPLETED' | head -n 120",
                cancellationToken).ConfigureAwait(false);

            var bootPkgs = Regex.Matches(receivers, @"([a-zA-Z0-9_.]+)/[a-zA-Z0-9_.$]+")
                .Select(m => m.Groups[1].Value)
                .Where(p => p.Contains('.'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(p => !OemWhitelist.IsLikelyOemOrPlatform(p))
                .ToList();

            progress?.Report(new ScanProgress
            {
                Phase = "Kalıcılık taranıyor",
                CurrentItem = $"{bootPkgs.Count} boot receiver",
                Percentage = 60
            });

            foreach (var pkg in bootPkgs)
            {
                var evidence = new List<ThreatEvidence>
                {
                    new()
                    {
                        Code = "boot_receiver",
                        Description = "BOOT_COMPLETED alıcısı",
                        ScoreWeight = 10
                    }
                };

                if (focusPackages.Contains(pkg, StringComparer.OrdinalIgnoreCase))
                {
                    evidence.Add(new ThreatEvidence
                    {
                        Code = "persist_with_threat",
                        Description = "Şüpheli paket ile kalıcılık korelasyonu",
                        ScoreWeight = 20
                    });
                }

                var item = RiskEngine.Finalize(new ThreatItem
                {
                    Name = $"Kalıcılık: {pkg}",
                    PackageName = pkg,
                    Type = ThreatType.Suspicious,
                    Category = ThreatCategory.Persistence,
                    Description = "Önyüklemede otomatik başlama",
                    SuspiciousActivities = ["BOOT_COMPLETED"],
                    Evidence = evidence,
                    RemediationAdvice = "Gereksiz boot alıcılarını kaldırın veya uygulamayı silin."
                });

                if (RiskEngine.MeetsThreatThreshold(item))
                    threats.Add(item);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Persistence tarama hatası");
        }

        progress?.Report(new ScanProgress
        {
            Phase = "Kalıcılık taranıyor",
            Percentage = 100,
            ThreatsFound = threats.Count
        });

        return threats;
    }
}
