using System.IO;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Security.Data;
using AndroidManager.Security.Engines;
using Serilog;

namespace AndroidManager.Security.Scanners;

public sealed class FileScanner
{
    private readonly IAdbService _adb;
    private readonly ThreatDatabase _db;

    private static readonly string[] ScanRoots =
    [
        "/data/local/tmp",
        "/sdcard/Download",
        "/sdcard/Downloads"
    ];

    public FileScanner(IAdbService adb, ThreatDatabase db)
    {
        _adb = adb;
        _db = db;
    }

    public async Task<List<ThreatItem>> ScanFilesAsync(
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var threats = new List<ThreatItem>();
        var i = 0;
        foreach (var root in ScanRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            i++;
            progress?.Report(new ScanProgress
            {
                Phase = "Dosyalar taranıyor",
                CurrentItem = root,
                Percentage = i * 100 / ScanRoots.Length,
                ThreatsFound = threats.Count
            });

            threats.AddRange(await ScanLocationAsync(root, cancellationToken).ConfigureAwait(false));
        }

        return threats
            .GroupBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(t => t.RiskScore).First())
            .ToList();
    }

    private async Task<List<ThreatItem>> ScanLocationAsync(string path, CancellationToken ct)
    {
        var threats = new List<ThreatItem>();
        try
        {
            var raw = await _adb.ExecuteShellAsync(
                $"find \"{path}\" -type f -maxdepth 3 2>/dev/null | head -n 80", ct)
                .ConfigureAwait(false);

            foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                ct.ThrowIfCancellationRequested();
                var filePath = line.Trim();
                if (string.IsNullOrWhiteSpace(filePath)) continue;

                var evidence = new List<ThreatEvidence>();
                var ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
                var name = System.IO.Path.GetFileName(filePath);

                try
                {
                    var (md5, sha256) = await HashHelper.HashRemoteAsync(_adb, filePath, ct).ConfigureAwait(false);
                    var def = _db.CheckHash(md5, sha256);
                    if (def is not null)
                    {
                        threats.Add(RiskEngine.Finalize(new ThreatItem
                        {
                            Name = def.Name,
                            FilePath = filePath,
                            HashMd5 = md5,
                            HashSha256 = sha256,
                            Type = def.Type,
                            Severity = def.Severity,
                            Category = ThreatCategory.File,
                            Description = def.Description,
                            Evidence =
                            [
                                new ThreatEvidence
                                {
                                    Code = "file_hash_ioc",
                                    Description = "Dosya hash IOC",
                                    ScoreWeight = 100
                                }
                            ],
                            RemediationAdvice = "Bu dosyayı silin veya karantinaya alın."
                        }));
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Dosya hash hatası: {Path}", filePath);
                }

                if (ext == ".apk" && !filePath.StartsWith("/data/app", StringComparison.OrdinalIgnoreCase))
                {
                    evidence.Add(new ThreatEvidence
                    {
                        Code = "loose_apk",
                        Description = "Depolamada gevşek APK",
                        ScoreWeight = 12
                    });
                }

                if (ext is ".sh" or ".elf")
                {
                    evidence.Add(new ThreatEvidence
                    {
                        Code = "script_binary",
                        Description = "Çalıştırılabilir script/binary",
                        ScoreWeight = 8
                    });
                }

                if (path.Contains("/data/local/tmp", StringComparison.OrdinalIgnoreCase) &&
                    ext is ".apk" or ".sh" or ".so" or ".dex")
                {
                    evidence.Add(new ThreatEvidence
                    {
                        Code = "tmp_payload",
                        Description = "tmp altında şüpheli yük",
                        ScoreWeight = 18
                    });
                }

                if (evidence.Count == 0) continue;

                var item = RiskEngine.Finalize(new ThreatItem
                {
                    Name = $"Dosya incelemesi: {name}",
                    FilePath = filePath,
                    Type = ThreatType.Suspicious,
                    Category = ThreatCategory.File,
                    Description = string.Join("; ", evidence.Select(e => e.Description)),
                    Evidence = evidence,
                    SuspiciousActivities = evidence.Select(e => e.Description).ToList(),
                    RemediationAdvice = "Dosyanın kaynağını doğrulayın; gerekirse silin."
                });

                if (RiskEngine.MeetsThreatThreshold(item))
                    threats.Add(item);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Dosya tarama hatası: {Path}", path);
        }

        return threats;
    }
}
