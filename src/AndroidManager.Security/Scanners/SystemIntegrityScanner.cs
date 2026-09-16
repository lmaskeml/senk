using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Security.Engines;
using Serilog;

namespace AndroidManager.Security.Scanners;

/// <summary>
/// Root flavor, verified boot, mount writability — reported as risk factors, not malware.
/// </summary>
public sealed class SystemIntegrityScanner
{
    private readonly IAdbService _adb;

    public SystemIntegrityScanner(IAdbService adb) => _adb = adb;

    public async Task<(List<ThreatItem> Threats, List<ThreatItem> RiskFactors)> ScanAsync(
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var threats = new List<ThreatItem>();
        var risks = new List<ThreatItem>();
        progress?.Report(new ScanProgress { Phase = "Sistem bütünlüğü", Percentage = 10 });

        risks.AddRange(await ScanRootAsync(cancellationToken).ConfigureAwait(false));
        progress?.Report(new ScanProgress { Phase = "Sistem bütünlüğü", Percentage = 40 });
        risks.AddRange(await ScanVerifiedBootAsync(cancellationToken).ConfigureAwait(false));
        progress?.Report(new ScanProgress { Phase = "Sistem bütünlüğü", Percentage = 70 });
        risks.AddRange(await ScanMountsAsync(cancellationToken).ConfigureAwait(false));
        progress?.Report(new ScanProgress { Phase = "Sistem bütünlüğü", Percentage = 100 });

        return (threats, risks);
    }

    private async Task<List<ThreatItem>> ScanRootAsync(CancellationToken ct)
    {
        var list = new List<ThreatItem>();
        try
        {
            var whichSu = await _adb.ExecuteShellAsync("which su 2>/dev/null; ls /system/xbin/su /system/bin/su 2>/dev/null", ct)
                .ConfigureAwait(false);
            var magisk = await _adb.ExecuteShellAsync(
                "pm path com.topjohnwu.magisk 2>/dev/null; getprop persist.sys.magisk.hide 2>/dev/null", ct)
                .ConfigureAwait(false);
            var kernelsu = await _adb.ExecuteShellAsync(
                "pm path me.weishu.kernelsu 2>/dev/null; ls /data/adb/ksu 2>/dev/null", ct)
                .ConfigureAwait(false);
            var apatch = await _adb.ExecuteShellAsync(
                "pm path me.bmax.apatch 2>/dev/null; ls /data/adb/ap 2>/dev/null", ct)
                .ConfigureAwait(false);

            var hasSu = whichSu.Contains("/su", StringComparison.OrdinalIgnoreCase);
            var hasMagisk = magisk.Contains("package:", StringComparison.OrdinalIgnoreCase) ||
                            magisk.Contains("magisk", StringComparison.OrdinalIgnoreCase);
            var hasKsu = kernelsu.Contains("package:", StringComparison.OrdinalIgnoreCase) ||
                         kernelsu.Contains("/ksu", StringComparison.OrdinalIgnoreCase);
            var hasApatch = apatch.Contains("package:", StringComparison.OrdinalIgnoreCase) ||
                            apatch.Contains("/ap", StringComparison.OrdinalIgnoreCase);

            if (!hasSu && !hasMagisk && !hasKsu && !hasApatch)
                return list;

            string flavor;
            int weight;
            if (hasMagisk) { flavor = "Magisk"; weight = 4; }
            else if (hasKsu) { flavor = "KernelSU"; weight = 4; }
            else if (hasApatch) { flavor = "APatch"; weight = 4; }
            else { flavor = "Bilinmeyen su"; weight = 18; }

            list.Add(new ThreatItem
            {
                Name = $"Root tespit edildi ({flavor})",
                Type = ThreatType.RiskFactor,
                Severity = flavor.StartsWith("Bilinmeyen", StringComparison.Ordinal) ? ThreatSeverity.High : ThreatSeverity.Info,
                Category = ThreatCategory.Integrity,
                Description = "Root = malware değildir; saldırı yüzeyini artırabilir.",
                Evidence =
                [
                    new ThreatEvidence
                    {
                        Code = "root_flavor",
                        Description = flavor,
                        ScoreWeight = weight
                    }
                ],
                RiskScore = weight,
                ConfidenceScore = 80,
                RemediationAdvice = flavor.StartsWith("Bilinmeyen", StringComparison.Ordinal)
                    ? "Bilinmeyen root uygulamasını inceleyin."
                    : "Root bilinen bir yönetici ise bilgi amaçlıdır."
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Root tarama hatası");
        }

        return list;
    }

    private async Task<List<ThreatItem>> ScanVerifiedBootAsync(CancellationToken ct)
    {
        var list = new List<ThreatItem>();
        try
        {
            var state = (await _adb.ExecuteShellAsync("getprop ro.boot.verifiedbootstate", ct).ConfigureAwait(false)).Trim();
            var locked = (await _adb.ExecuteShellAsync("getprop ro.boot.flash.locked", ct).ConfigureAwait(false)).Trim();
            var vbmeta = (await _adb.ExecuteShellAsync("getprop ro.boot.vbmeta.device_state", ct).ConfigureAwait(false)).Trim();

            if (!string.IsNullOrWhiteSpace(state) &&
                !state.Equals("green", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new ThreatItem
                {
                    Name = "Verified Boot yeşil değil",
                    Type = ThreatType.RiskFactor,
                    Severity = ThreatSeverity.Medium,
                    Category = ThreatCategory.Integrity,
                    Description = $"ro.boot.verifiedbootstate={state}",
                    Evidence =
                    [
                        new ThreatEvidence
                        {
                            Code = "vb_state",
                            Description = state,
                            ScoreWeight = 10
                        }
                    ],
                    RiskScore = 10,
                    ConfidenceScore = 90,
                    RemediationAdvice = "Bootloader / vbmeta durumunu doğrulayın."
                });
            }

            if (locked is "0" or "false")
            {
                list.Add(new ThreatItem
                {
                    Name = "Bootloader kilitli değil",
                    Type = ThreatType.RiskFactor,
                    Severity = ThreatSeverity.Medium,
                    Category = ThreatCategory.Integrity,
                    Description = $"ro.boot.flash.locked={locked}; vbmeta={vbmeta}",
                    Evidence =
                    [
                        new ThreatEvidence
                        {
                            Code = "bl_unlocked",
                            Description = locked,
                            ScoreWeight = 12
                        }
                    ],
                    RiskScore = 12,
                    ConfidenceScore = 90,
                    RemediationAdvice = "Kilit açma bilinçli değilse risk faktörüdür."
                });
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Verified boot tarama hatası");
        }

        return list;
    }

    private async Task<List<ThreatItem>> ScanMountsAsync(CancellationToken ct)
    {
        var list = new List<ThreatItem>();
        try
        {
            var mounts = await _adb.ExecuteShellAsync("mount", ct).ConfigureAwait(false);
            foreach (var part in new[] { "/system", "/vendor", "/product", "/system_ext" })
            {
                var line = mounts.Split('\n')
                    .FirstOrDefault(l => l.Contains($" {part} ", StringComparison.OrdinalIgnoreCase));
                if (line is null) continue;
                if (!line.Contains("rw,", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("(rw,", StringComparison.OrdinalIgnoreCase))
                    continue;

                list.Add(new ThreatItem
                {
                    Name = $"Yazılabilir bölüm: {part}",
                    Type = ThreatType.RiskFactor,
                    Severity = ThreatSeverity.High,
                    Category = ThreatCategory.Integrity,
                    Description = line.Trim(),
                    Evidence =
                    [
                        new ThreatEvidence
                        {
                            Code = "mount_rw",
                            Description = part,
                            ScoreWeight = 15
                        }
                    ],
                    RiskScore = 15,
                    ConfidenceScore = 75,
                    RemediationAdvice = "Sistem bölümünün yazılabilir olması olağan dışı olabilir."
                });
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Mount tarama hatası");
        }

        return list;
    }
}
