using System.IO;
using System.Text.Json;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Security.Data;

public sealed class ThreatDatabase
{
    private static readonly string BundledDbPath = System.IO.Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "security", "threat_definitions.json");

    private static readonly string UserDbPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AndroidManager", "security", "custom_definitions.json");

    private ThreatDefinitions _definitions = new();

    public DateTime LastUpdated => _definitions.LastUpdated;
    public string Version => _definitions.Version;
    public int TotalEntries =>
        _definitions.KnownMalware.Count +
        _definitions.SuspiciousPackages.Count +
        _definitions.CertificateIocs.Count;

    public IReadOnlyList<StringRule> StringRules => _definitions.StringRules;
    public IReadOnlyList<string> KnownC2Servers => _definitions.KnownC2Servers;
    public IReadOnlyList<string> KnownC2Ips => _definitions.KnownC2Ips;

    public void Load()
    {
        try
        {
            if (File.Exists(BundledDbPath))
            {
                var json = File.ReadAllText(BundledDbPath);
                _definitions = JsonSerializer.Deserialize<ThreatDefinitions>(json) ?? GetBuiltinDefinitions();
            }
            else
            {
                _definitions = GetBuiltinDefinitions();
            }

            if (File.Exists(UserDbPath))
            {
                var json = File.ReadAllText(UserDbPath);
                var custom = JsonSerializer.Deserialize<ThreatDefinitions>(json);
                if (custom is not null)
                    MergeDefinitions(custom);
            }

            Log.Information("Tehdit DB yüklendi: {Count} giriş v{Version}", TotalEntries, Version);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Tehdit DB yükleme hatası");
            _definitions = GetBuiltinDefinitions();
        }
    }

    public ThreatDefinition? CheckHash(string? md5, string? sha256)
    {
        if (!string.IsNullOrWhiteSpace(sha256))
        {
            var bySha = _definitions.KnownMalware.FirstOrDefault(m =>
                !string.IsNullOrWhiteSpace(m.HashSha256) &&
                m.HashSha256.Equals(sha256, StringComparison.OrdinalIgnoreCase));
            if (bySha is not null) return bySha;
        }

        if (!string.IsNullOrWhiteSpace(md5))
        {
            return _definitions.KnownMalware.FirstOrDefault(m =>
                !string.IsNullOrWhiteSpace(m.HashMd5) &&
                m.HashMd5.Equals(md5, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    public ThreatDefinition? CheckPackage(string? packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName)) return null;

        var exact = _definitions.KnownMalware.FirstOrDefault(m =>
            !string.IsNullOrWhiteSpace(m.PackageName) &&
            m.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        return _definitions.SuspiciousPackages.FirstOrDefault(m =>
            !string.IsNullOrWhiteSpace(m.PackageName) &&
            m.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase));
    }

    public ThreatDefinition? CheckCertificate(string? certSha256)
    {
        if (string.IsNullOrWhiteSpace(certSha256)) return null;
        return _definitions.CertificateIocs.FirstOrDefault(c =>
            !string.IsNullOrWhiteSpace(c.CertificateSha256) &&
            c.CertificateSha256.Equals(certSha256, StringComparison.OrdinalIgnoreCase));
    }

    public List<PermissionRisk> AnalyzePermissions(IReadOnlyList<string> permissions) =>
        _definitions.DangerousPermissions
            .Where(p => permissions.Contains(p.Permission, StringComparer.OrdinalIgnoreCase))
            .ToList();

    public List<PermissionCombination> CheckDangerousCombinations(IReadOnlyList<string> permissions) =>
        _definitions.DangerousCombinations
            .Where(combo => combo.Permissions.All(p =>
                permissions.Contains(p, StringComparer.OrdinalIgnoreCase)))
            .ToList();

    public bool IsKnownC2(string hostOrIp)
    {
        if (string.IsNullOrWhiteSpace(hostOrIp)) return false;
        if (_definitions.KnownC2Ips.Any(ip => hostOrIp.Contains(ip, StringComparison.OrdinalIgnoreCase)))
            return true;
        return _definitions.KnownC2Servers.Any(c2 =>
            hostOrIp.Contains(c2, StringComparison.OrdinalIgnoreCase));
    }

    private void MergeDefinitions(ThreatDefinitions custom)
    {
        _definitions.KnownMalware.AddRange(custom.KnownMalware);
        _definitions.SuspiciousPackages.AddRange(custom.SuspiciousPackages);
        _definitions.CertificateIocs.AddRange(custom.CertificateIocs);
        _definitions.KnownC2Servers.AddRange(custom.KnownC2Servers);
        _definitions.KnownC2Ips.AddRange(custom.KnownC2Ips);
        _definitions.StringRules.AddRange(custom.StringRules);
        if (!string.IsNullOrWhiteSpace(custom.Version))
            _definitions.Version = custom.Version;
        if (custom.LastUpdated > _definitions.LastUpdated)
            _definitions.LastUpdated = custom.LastUpdated;
    }

    public static ThreatDefinitions GetBuiltinDefinitions() => new()
    {
        Version = "1.1.0",
        LastUpdated = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
        KnownMalware =
        [
            new() { Name = "Joker Malware", PackageName = "com.imagecompress.android", Type = ThreatType.Malware, Severity = ThreatSeverity.Critical, Description = "SMS abonelik dolandırıcısı" },
            new() { Name = "BankBot", PackageName = "com.android.google", Type = ThreatType.Trojan, Severity = ThreatSeverity.Critical, Description = "Bankacılık trojanı göstergesi" },
            new() { Name = "Cerberus RAT", PackageName = "com.security.service", Type = ThreatType.Spyware, Severity = ThreatSeverity.Critical, Description = "Uzaktan erişim trojanı" },
            new() { Name = "SpinOk Adware", PackageName = "com.spin.sdk", Type = ThreatType.Adware, Severity = ThreatSeverity.High, Description = "Veri toplayan reklam SDK" },
            new() { Name = "GriftHorse", PackageName = "com.easydownloader.all", Type = ThreatType.Trojan, Severity = ThreatSeverity.Critical, Description = "WAP abonelik dolandırıcısı" },
            new() { Name = "FluBot", PackageName = "com.fedex.delivery", Type = ThreatType.Malware, Severity = ThreatSeverity.Critical, Description = "Kargo takibi gibi görünen SMS yayıcı" },
            new() { Name = "Anubis Banking Trojan", PackageName = "com.adobe.flash", Type = ThreatType.Trojan, Severity = ThreatSeverity.Critical, Description = "Sahte Flash güncellemesi" },
            new() { Name = "xHelper", PackageName = "com.mufc.umbtts", Type = ThreatType.Trojan, Severity = ThreatSeverity.Critical, Description = "Kaldırılması zor trojan dropper" },
        ],
        SuspiciousPackages =
        [
            new() { Name = "Sahte güncelleyici", PackageName = "com.android.updater", Type = ThreatType.Suspicious, Severity = ThreatSeverity.Medium, Description = "Sistem güncellemesi gibi görünen 3. parti paket" },
            new() { Name = "Sahte Play Store", PackageName = "com.android.vending.update", Type = ThreatType.Pua, Severity = ThreatSeverity.High, Description = "Play Store taklidi" },
        ],
        DangerousPermissions =
        [
            new("android.permission.SEND_SMS", "SMS Gönderme", ThreatSeverity.Critical, "Ücretli numaralara SMS gönderebilir"),
            new("android.permission.RECEIVE_SMS", "SMS Alma", ThreatSeverity.High, "Gelen SMS'leri ele geçirebilir"),
            new("android.permission.READ_SMS", "SMS Okuma", ThreatSeverity.High, "SMS mesajlarını okuyabilir"),
            new("android.permission.RECORD_AUDIO", "Mikrofon", ThreatSeverity.High, "Arka planda ses kaydı"),
            new("android.permission.CAMERA", "Kamera", ThreatSeverity.High, "Arka planda kamera kullanımı"),
            new("android.permission.ACCESS_FINE_LOCATION", "Konum", ThreatSeverity.Medium, "Hassas konum"),
            new("android.permission.READ_CONTACTS", "Rehber", ThreatSeverity.Medium, "Rehber erişimi"),
            new("android.permission.READ_CALL_LOG", "Arama geçmişi", ThreatSeverity.High, "Arama logu"),
            new("android.permission.INSTALL_PACKAGES", "Uygulama kurma", ThreatSeverity.Critical, "Paket kurulum yetkisi"),
            new("android.permission.REQUEST_INSTALL_PACKAGES", "Kurulum isteği", ThreatSeverity.High, "Bilinmeyen kaynak kurulumu"),
            new("android.permission.BIND_ACCESSIBILITY_SERVICE", "Erişilebilirlik", ThreatSeverity.Critical, "Ekran okuma / tıklama"),
            new("android.permission.BIND_DEVICE_ADMIN", "Cihaz yöneticisi", ThreatSeverity.Critical, "Cihaz kilitleme/silme"),
            new("android.permission.WRITE_SETTINGS", "Sistem ayarları", ThreatSeverity.High, "Sistem ayarı değişikliği"),
            new("android.permission.GET_ACCOUNTS", "Hesaplar", ThreatSeverity.Medium, "Hesap listesi"),
        ],
        DangerousCombinations =
        [
            new()
            {
                Name = "SMS abonelik dolandırıcısı",
                Permissions = ["android.permission.SEND_SMS", "android.permission.RECEIVE_SMS", "android.permission.INTERNET"],
                Severity = ThreatSeverity.Critical,
                Description = "Ücretli SMS aboneliği riski",
                ScoreWeight = 40
            },
            new()
            {
                Name = "Bankacılık trojan göstergesi",
                Permissions = ["android.permission.BIND_ACCESSIBILITY_SERVICE", "android.permission.READ_SMS", "android.permission.INTERNET"],
                Severity = ThreatSeverity.Critical,
                Description = "Accessibility + SMS + ağ kombinasyonu",
                ScoreWeight = 45
            },
            new()
            {
                Name = "Casus yazılım göstergesi",
                Permissions =
                [
                    "android.permission.RECORD_AUDIO", "android.permission.CAMERA",
                    "android.permission.ACCESS_FINE_LOCATION", "android.permission.READ_CONTACTS"
                ],
                Severity = ThreatSeverity.Critical,
                Description = "Kapsamlı gözetleme izin seti",
                ScoreWeight = 35
            },
            new()
            {
                Name = "Cihaz yöneticisi kötüye kullanımı",
                Permissions = ["android.permission.BIND_DEVICE_ADMIN", "android.permission.INTERNET"],
                Severity = ThreatSeverity.Critical,
                Description = "Uzaktan cihaz kontrolü riski",
                ScoreWeight = 40
            },
        ],
        KnownC2Servers =
        [
            "cerberus-android.pw", "anubis-panel.xyz", "flubot-c2.com",
            "joker-sub.net", "grifthorse-pay.com", "bankbot-panel.ru", "xhelper-drop.cn"
        ],
        KnownC2Ips = [],
        StringRules =
        [
            new() { Id = "exec_runtime", Name = "Runtime.exec", Pattern = @"Runtime\.getRuntime\(\)\.exec", ScoreWeight = 12, Severity = ThreatSeverity.Medium, Description = "Kabuk komutu çalıştırma" },
            new() { Id = "onion", Name = "Onion adres", Pattern = @"\.onion", ScoreWeight = 15, Severity = ThreatSeverity.High, Description = "Tor onion göstergesi" },
            new() { Id = "raw_ip_url", Name = "Ham IP URL", Pattern = @"https?://\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}", ScoreWeight = 8, Severity = ThreatSeverity.Low, Description = "Doğrudan IP üzerinden HTTP" },
        ]
    };
}
