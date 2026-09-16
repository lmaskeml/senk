using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Security.Data;
using AndroidManager.Security.Engines;
using Serilog;

namespace AndroidManager.Security.Scanners;

public sealed class AppScanner
{
    private readonly IAdbService _adb;
    private readonly IAdbSyncService _sync;
    private readonly ThreatDatabase _db;
    private readonly RuleEngine _rules;

    public AppScanner(IAdbService adb, IAdbSyncService sync, ThreatDatabase db)
    {
        _adb = adb;
        _sync = sync;
        _db = db;
        _rules = new RuleEngine(db);
    }

    public async Task<List<ThreatItem>> ScanAllAppsAsync(
        IProgress<ScanProgress>? progress,
        bool deepScan,
        bool rescueMode,
        CancellationToken cancellationToken)
    {
        var threats = new List<ThreatItem>();
        var raw = await _adb.ExecuteShellAsync("pm list packages -f -3", cancellationToken).ConfigureAwait(false);
        if (rescueMode)
        {
            var all = await _adb.ExecuteShellAsync("pm list packages -f", cancellationToken).ConfigureAwait(false);
            raw = all;
        }

        var packages = ParsePackages(raw);
        var total = Math.Max(1, packages.Count);
        var scanned = 0;

        foreach (var (pkg, apkPath, _) in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned++;
            progress?.Report(new ScanProgress
            {
                Phase = "Uygulamalar taranıyor",
                CurrentItem = pkg,
                Percentage = scanned * 100 / total,
                ScannedCount = scanned,
                TotalCount = total,
                ThreatsFound = threats.Count
            });

            if (OemWhitelist.IsLikelyOemOrPlatform(pkg) && !rescueMode)
                continue;

            var findings = await ScanPackageAsync(pkg, apkPath, deepScan, cancellationToken).ConfigureAwait(false);
            threats.AddRange(findings);
        }

        return threats;
    }

    private async Task<List<ThreatItem>> ScanPackageAsync(
        string packageName,
        string apkPath,
        bool deepScan,
        CancellationToken cancellationToken)
    {
        var findings = new List<ThreatItem>();
        var evidence = new List<ThreatEvidence>();

        var pkgDef = _db.CheckPackage(packageName);
        if (pkgDef is not null)
        {
            evidence.Add(new ThreatEvidence
            {
                Code = "pkg_ioc",
                Description = $"Paket IOC: {pkgDef.Name}",
                ScoreWeight = 100
            });

            findings.Add(RiskEngine.Finalize(new ThreatItem
            {
                Name = pkgDef.Name,
                PackageName = packageName,
                FilePath = apkPath,
                Type = pkgDef.Type,
                Severity = pkgDef.Severity,
                Category = ThreatCategory.App,
                Description = pkgDef.Description,
                Evidence = evidence,
                RemediationAdvice = Remediation(pkgDef.Type)
            }));
            return findings;
        }

        string md5 = "", sha256 = "";
        if (!string.IsNullOrWhiteSpace(apkPath) && apkPath is not "unknown")
        {
            try
            {
                (md5, sha256) = await HashHelper.HashRemoteAsync(_adb, apkPath, cancellationToken)
                    .ConfigureAwait(false);
                var hashDef = _db.CheckHash(md5, sha256);
                if (hashDef is not null)
                {
                    findings.Add(RiskEngine.Finalize(new ThreatItem
                    {
                        Name = hashDef.Name,
                        PackageName = packageName,
                        FilePath = apkPath,
                        HashMd5 = md5,
                        HashSha256 = sha256,
                        Type = hashDef.Type,
                        Severity = hashDef.Severity,
                        Category = ThreatCategory.App,
                        Description = hashDef.Description,
                        Evidence =
                        [
                            new ThreatEvidence
                            {
                                Code = "hash_ioc",
                                Description = "APK hash IOC eşleşmesi",
                                ScoreWeight = 100
                            }
                        ],
                        RemediationAdvice = Remediation(hashDef.Type)
                    }));
                    return findings;
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Hash alınamadı: {Pkg}", packageName);
            }
        }

        var installer = await GetInstallerAsync(packageName, cancellationToken).ConfigureAwait(false);
        if (!IsKnownInstaller(installer) && !string.IsNullOrWhiteSpace(installer) && installer != "null")
        {
            evidence.Add(new ThreatEvidence
            {
                Code = "unknown_installer",
                Description = $"Bilinmeyen kurulum kaynağı: {installer}",
                ScoreWeight = 10
            });
        }

        var permissions = await GetPermissionsAsync(packageName, cancellationToken).ConfigureAwait(false);
        var combos = _db.CheckDangerousCombinations(permissions);
        foreach (var combo in combos)
        {
            evidence.Add(new ThreatEvidence
            {
                Code = "perm_combo",
                Description = combo.Name,
                ScoreWeight = combo.ScoreWeight
            });
        }

        var risks = _db.AnalyzePermissions(permissions);
        if (risks.Count(r => r.Severity >= ThreatSeverity.High) >= 3)
        {
            evidence.Add(new ThreatEvidence
            {
                Code = "perm_cluster",
                Description = $"{risks.Count} tehlikeli izin",
                ScoreWeight = 12
            });
        }

        if (deepScan && !string.IsNullOrWhiteSpace(apkPath) && apkPath is not "unknown")
        {
            var deepEvidence = await DeepScanApkAsync(packageName, apkPath, cancellationToken)
                .ConfigureAwait(false);
            evidence.AddRange(deepEvidence.Evidence);
            foreach (var domain in deepEvidence.C2Hits)
            {
                findings.Add(RiskEngine.Finalize(new ThreatItem
                {
                    Name = $"Bilinen C&C göstergesi: {packageName}",
                    PackageName = packageName,
                    FilePath = apkPath,
                    Type = ThreatType.Malware,
                    Severity = ThreatSeverity.Critical,
                    Category = ThreatCategory.Network,
                    Description = $"Statik analizde bilinen C&C: {domain}",
                    NetworkIndicators = [domain],
                    HashMd5 = md5,
                    HashSha256 = sha256,
                    Evidence =
                    [
                        new ThreatEvidence
                        {
                            Code = "static_c2",
                            Description = domain,
                            ScoreWeight = 40
                        }
                    ],
                    RemediationAdvice = "Uygulamayı kaldırın ve ağ bağlantılarını doğrulayın."
                }));
            }
        }

        if (evidence.Count == 0)
            return findings;

        var draft = new ThreatItem
        {
            Name = $"Davranış riski: {packageName}",
            PackageName = packageName,
            FilePath = apkPath,
            HashMd5 = md5,
            HashSha256 = sha256,
            Type = ThreatType.Suspicious,
            Category = ThreatCategory.App,
            Description = string.Join("; ", evidence.Select(e => e.Description).Take(4)),
            SuspiciousPermissions = combos.SelectMany(c => c.Permissions).Distinct().ToList(),
            SuspiciousActivities = evidence.Select(e => e.Description).ToList(),
            Evidence = evidence,
            RemediationAdvice = "Birden fazla kanıt birikti; uygulamayı inceleyin veya kaldırın."
        };

        var finalized = RiskEngine.Finalize(draft);
        if (RiskEngine.MeetsThreatThreshold(finalized))
            findings.Add(finalized);

        return findings;
    }

    private async Task<(List<ThreatEvidence> Evidence, List<string> C2Hits)> DeepScanApkAsync(
        string packageName,
        string apkPath,
        CancellationToken cancellationToken)
    {
        var evidence = new List<ThreatEvidence>();
        var c2 = new List<string>();
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"am_sec_{Guid.NewGuid():N}.apk");

        try
        {
            await _sync.PullAsync(apkPath, temp, null, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                return (evidence, c2);

            using var zip = ZipFile.OpenRead(temp);
            foreach (var dex in zip.Entries.Where(e => e.Name.EndsWith(".dex", StringComparison.OrdinalIgnoreCase)))
            {
                await using var stream = dex.Open();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                var content = HashHelper.ExtractAsciiStrings(ms.ToArray());
                evidence.AddRange(_rules.Match(content));

                foreach (var domain in ExtractDomains(content))
                {
                    if (_db.IsKnownC2(domain))
                        c2.Add(domain);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Derin APK analizi başarısız: {Pkg}", packageName);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
        }

        return (evidence, c2.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private async Task<List<string>> GetPermissionsAsync(string packageName, CancellationToken ct)
    {
        try
        {
            var dump = await _adb.ExecuteShellAsync($"dumpsys package {packageName}", ct).ConfigureAwait(false);
            var perms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(dump, @"android\.permission\.[A-Z0-9_]+"))
                perms.Add(m.Value);
            foreach (Match m in Regex.Matches(dump, @"android\.permission\.BIND_[A-Z0-9_]+"))
                perms.Add(m.Value);
            return perms.ToList();
        }
        catch
        {
            return [];
        }
    }

    private async Task<string> GetInstallerAsync(string packageName, CancellationToken ct)
    {
        try
        {
            var raw = await _adb.ExecuteShellAsync(
                $"pm list packages -i {packageName}", ct).ConfigureAwait(false);
            var m = Regex.Match(raw, @"installer=(\S+)");
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }
        catch
        {
            return "";
        }
    }

    private static List<(string pkg, string apk, string installer)> ParsePackages(string raw)
    {
        var list = new List<(string, string, string)>();
        foreach (Match m in Regex.Matches(raw, @"package:(.+?)=([^\s]+)"))
        {
            list.Add((m.Groups[2].Value.Trim(), m.Groups[1].Value.Trim(), "unknown"));
        }

        return list
            .GroupBy(x => x.Item1, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static List<string> ExtractDomains(string content)
    {
        var regex = new Regex(
            @"(?:https?://)?([a-zA-Z0-9\-]+\.[a-zA-Z]{2,}(?:\.[a-zA-Z]{2,})?)",
            RegexOptions.IgnoreCase);
        return regex.Matches(content)
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .Distinct()
            .ToList();
    }

    private static bool IsKnownInstaller(string installer) =>
        installer is "com.android.vending"
            or "com.amazon.venezia"
            or "com.samsung.android.app.galaxyapps"
            or "org.fdroid.fdroid"
            or "com.huawei.appmarket"
            or "com.sec.android.app.samsungapps";

    private static string Remediation(ThreatType type) => type switch
    {
        ThreatType.Malware => "Uygulamayı kaldırın ve hesap güvenliğini kontrol edin.",
        ThreatType.Spyware => "Kaldırın ve kritik şifreleri değiştirin.",
        ThreatType.Trojan => "Kaldırın; bankacılık uygulamalarını doğrulayın.",
        ThreatType.Adware => "Uygulamayı kaldırmanız önerilir.",
        ThreatType.Rootkit => "Kalıcı rootkit için fabrika sıfırlama gerekebilir.",
        ThreatType.Pua => "Gereksizse kaldırın.",
        _ => "Manuel inceleme yapın."
    };
}
