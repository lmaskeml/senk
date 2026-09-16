using System.Text.RegularExpressions;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Security.Engines;
using Serilog;

namespace AndroidManager.Security.Scanners;

/// <summary>
/// Accessibility, Device Admin, notification listeners, VPN, IME, default apps.
/// </summary>
public sealed class PrivilegeScanner
{
    private readonly IAdbService _adb;

    public PrivilegeScanner(IAdbService adb) => _adb = adb;

    public async Task<List<ThreatItem>> ScanAsync(
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var threats = new List<ThreatItem>();
        progress?.Report(new ScanProgress { Phase = "Ayrıcalıklar taranıyor", Percentage = 10 });

        threats.AddRange(await ScanAccessibilityAsync(cancellationToken).ConfigureAwait(false));
        progress?.Report(new ScanProgress { Phase = "Ayrıcalıklar taranıyor", Percentage = 35 });
        threats.AddRange(await ScanDeviceAdminsAsync(cancellationToken).ConfigureAwait(false));
        progress?.Report(new ScanProgress { Phase = "Ayrıcalıklar taranıyor", Percentage = 55 });
        threats.AddRange(await ScanNotificationListenersAsync(cancellationToken).ConfigureAwait(false));
        progress?.Report(new ScanProgress { Phase = "Ayrıcalıklar taranıyor", Percentage = 75 });
        threats.AddRange(await ScanVpnAndImeAsync(cancellationToken).ConfigureAwait(false));
        progress?.Report(new ScanProgress
        {
            Phase = "Ayrıcalıklar taranıyor",
            Percentage = 100,
            ThreatsFound = threats.Count
        });

        return threats;
    }

    private async Task<List<ThreatItem>> ScanAccessibilityAsync(CancellationToken ct)
    {
        var list = new List<ThreatItem>();
        try
        {
            var raw = await _adb.ExecuteShellAsync(
                "settings get secure enabled_accessibility_services", ct).ConfigureAwait(false);
            foreach (var entry in SplitServices(raw))
            {
                var pkg = PackageFromComponent(entry);
                if (string.IsNullOrWhiteSpace(pkg) || OemWhitelist.IsLikelyOemOrPlatform(pkg))
                    continue;

                list.Add(RiskEngine.Finalize(new ThreatItem
                {
                    Name = $"Erişilebilirlik servisi: {pkg}",
                    PackageName = pkg,
                    Type = ThreatType.Suspicious,
                    Category = ThreatCategory.Privilege,
                    Description = "Aktif accessibility servisi — ekran okuma/tıklama yetkisi",
                    SuspiciousActivities = [entry],
                    Evidence =
                    [
                        new ThreatEvidence
                        {
                            Code = "accessibility",
                            Description = entry,
                            ScoreWeight = 20
                        }
                    ],
                    RemediationAdvice = "Gereksizse Ayarlar > Erişilebilirlik üzerinden kapatın; şüpheliyse kaldırın."
                }));
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Accessibility tarama hatası");
        }

        return list.Where(RiskEngine.MeetsThreatThreshold).ToList();
    }

    private async Task<List<ThreatItem>> ScanDeviceAdminsAsync(CancellationToken ct)
    {
        var list = new List<ThreatItem>();
        try
        {
            var raw = await _adb.ExecuteShellAsync("dumpsys device_policy", ct).ConfigureAwait(false);
            foreach (Match m in Regex.Matches(raw, @"admin=\{?([^}/\s]+)"))
            {
                var pkg = PackageFromComponent(m.Groups[1].Value);
                if (string.IsNullOrWhiteSpace(pkg) || OemWhitelist.IsLikelyOemOrPlatform(pkg))
                    continue;

                list.Add(RiskEngine.Finalize(new ThreatItem
                {
                    Name = $"Cihaz yöneticisi: {pkg}",
                    PackageName = pkg,
                    Type = ThreatType.Suspicious,
                    Severity = ThreatSeverity.High,
                    Category = ThreatCategory.Privilege,
                    Description = "Aktif cihaz yöneticisi — kilitleme/silme riski",
                    Evidence =
                    [
                        new ThreatEvidence
                        {
                            Code = "device_admin",
                            Description = pkg,
                            ScoreWeight = 25
                        }
                    ],
                    RemediationAdvice = "Şüpheliyse cihaz yöneticisini kaldırın, sonra uygulamayı silin."
                }));
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Device admin tarama hatası");
        }

        return list
            .GroupBy(t => t.PackageName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Where(RiskEngine.MeetsThreatThreshold)
            .ToList();
    }

    private async Task<List<ThreatItem>> ScanNotificationListenersAsync(CancellationToken ct)
    {
        var list = new List<ThreatItem>();
        try
        {
            var raw = await _adb.ExecuteShellAsync(
                "settings get secure enabled_notification_listeners", ct).ConfigureAwait(false);
            foreach (var entry in SplitServices(raw))
            {
                var pkg = PackageFromComponent(entry);
                if (string.IsNullOrWhiteSpace(pkg) || OemWhitelist.IsLikelyOemOrPlatform(pkg))
                    continue;

                list.Add(RiskEngine.Finalize(new ThreatItem
                {
                    Name = $"Bildirim dinleyicisi: {pkg}",
                    PackageName = pkg,
                    Type = ThreatType.Suspicious,
                    Category = ThreatCategory.Privilege,
                    Description = "Bildirim içeriğine erişim",
                    Evidence =
                    [
                        new ThreatEvidence
                        {
                            Code = "notif_listener",
                            Description = entry,
                            ScoreWeight = 12
                        }
                    ],
                    RemediationAdvice = "Gereksiz bildirim erişimini kapatın."
                }));
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Notification listener tarama hatası");
        }

        return list.Where(RiskEngine.MeetsThreatThreshold).ToList();
    }

    private async Task<List<ThreatItem>> ScanVpnAndImeAsync(CancellationToken ct)
    {
        var list = new List<ThreatItem>();
        try
        {
            var vpn = await _adb.ExecuteShellAsync(
                "dumpsys connectivity | grep -i 'VPN\\|NetworkAgentInfo' | head -n 40", ct)
                .ConfigureAwait(false);
            if (vpn.Contains("VPN", StringComparison.OrdinalIgnoreCase) &&
                !vpn.Contains("Type: WIFI", StringComparison.OrdinalIgnoreCase))
            {
                // informational risk factor handled lightly
                list.Add(RiskEngine.Finalize(new ThreatItem
                {
                    Name = "Aktif VPN oturumu",
                    Type = ThreatType.RiskFactor,
                    Severity = ThreatSeverity.Low,
                    Category = ThreatCategory.Privilege,
                    Description = "Aktif VPN tespit edildi (meşru olabilir)",
                    Evidence =
                    [
                        new ThreatEvidence
                        {
                            Code = "vpn_active",
                            Description = "VPN",
                            ScoreWeight = 6
                        }
                    ],
                    RemediationAdvice = "VPN sağlayıcısını doğrulayın."
                }));
            }

            var ime = await _adb.ExecuteShellAsync(
                "settings get secure default_input_method", ct).ConfigureAwait(false);
            var imePkg = PackageFromComponent(ime.Trim());
            if (!string.IsNullOrWhiteSpace(imePkg) && !OemWhitelist.IsLikelyOemOrPlatform(imePkg))
            {
                list.Add(RiskEngine.Finalize(new ThreatItem
                {
                    Name = $"3. parti klavye: {imePkg}",
                    PackageName = imePkg,
                    Type = ThreatType.Suspicious,
                    Category = ThreatCategory.Privilege,
                    Description = "Varsayılan IME üçüncü parti",
                    Evidence =
                    [
                        new ThreatEvidence
                        {
                            Code = "third_party_ime",
                            Description = imePkg,
                            ScoreWeight = 10
                        }
                    ],
                    RemediationAdvice = "Klavye uygulamasının güvenilir olduğundan emin olun."
                }));
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "VPN/IME tarama hatası");
        }

        return list.Where(t => t.Type != ThreatType.RiskFactor || t.RiskScore >= 6).ToList();
    }

    private static IEnumerable<string> SplitServices(string raw) =>
        (raw ?? "")
            .Replace('\n', ':')
            .Split([':', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Contains('/') || s.Contains('.'));

    private static string PackageFromComponent(string component)
    {
        var value = component.Trim().Trim('{', '}');
        var slash = value.IndexOf('/');
        if (slash > 0) return value[..slash];
        var slash2 = value.IndexOf('/');
        return slash2 > 0 ? value[..slash2] : value.Split(':').FirstOrDefault() ?? value;
    }
}
