using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Security.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AndroidManager.Security.ViewModels;

public sealed partial class VirusTotalViewModel : ObservableObject
{
    private static readonly string ScansDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AndroidManager", "security", "scans");

    private readonly IVirusTotalService _vt;
    private readonly IAdbService _adb;
    private readonly IAdbSyncService _sync;
    private readonly INotificationService _notification;
    private readonly IAppDialogService _dialogs;
    private readonly ILogger _logger;

    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private bool _useSimulation;
    [ObservableProperty] private bool _autoUpgrade = true;
    [ObservableProperty] private string _manualHashes = "";
    [ObservableProperty] private ObservableCollection<VtItemViewModel> _items = [];
    [ObservableProperty] private VtStatus? _status;
    [ObservableProperty] private string _statusMessage = "Hazır";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _progressPercent;
    [ObservableProperty] private string _progressText = "";

    public VirusTotalViewModel(
        IVirusTotalService vt,
        IAdbService adb,
        IAdbSyncService sync,
        INotificationService notification,
        IAppDialogService dialogs)
    {
        _vt = vt;
        _adb = adb;
        _sync = sync;
        _notification = notification;
        _dialogs = dialogs;
        _logger = Log.ForContext<VirusTotalViewModel>();
    }

    public async Task InitializeAsync()
    {
        ApiKey = _vt.Config.ApiKey;
        UseSimulation = _vt.Config.UseSimulation;
        AutoUpgrade = _vt.Config.AutoUpgradeThreats;
        Status = await _vt.GetStatusAsync();
    }

    [RelayCommand]
    private async Task SaveConfigAsync()
    {
        IsBusy = true;
        try
        {
            await _vt.SaveConfigAsync(new VtConfig
            {
                ApiKey = ApiKey.Trim(),
                UseSimulation = UseSimulation,
                AutoUpgradeThreats = AutoUpgrade,
                MinRequestIntervalMs = _vt.Config.MinRequestIntervalMs,
                DailyQuota = _vt.Config.DailyQuota,
                MaliciousThreshold = _vt.Config.MaliciousThreshold,
                UploadPollSeconds = _vt.Config.UploadPollSeconds
            });

            Status = await _vt.GetStatusAsync();
            StatusMessage = UseSimulation
                ? "Simülasyon modu aktif — sahte raporlar üretilecek"
                : $"Ayarlar kaydedildi ({Status?.QuotaText})";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadFromScanHistoryAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            if (!Directory.Exists(ScansDir))
            {
                StatusMessage = "Tarama geçmişi yok — önce Güvenlik sekmesinden tarama yapın";
                return;
            }

            var files = Directory.GetFiles(ScansDir, "scan_*.json")
                .OrderByDescending(f => f)
                .Take(5)
                .ToList();

            var added = 0;
            foreach (var file in files)
            {
                var session = JsonSerializer.Deserialize<ScanSession>(
                    await File.ReadAllTextAsync(file, ct));
                if (session?.Threats is null)
                    continue;

                foreach (var threat in session.Threats)
                {
                    var hash = FirstNonEmpty(threat.HashSha256, threat.HashMd5);
                    if (!string.IsNullOrEmpty(hash) && !ContainsHash(hash))
                    {
                        Items.Add(new VtItemViewModel(
                            hash, threat.Name, threat.FilePath, threat.PackageName));
                        added++;
                        continue;
                    }

                    foreach (var (h, path) in await ComputeHashForThreatAsync(threat, ct))
                    {
                        if (ContainsHash(h))
                            continue;
                        Items.Add(new VtItemViewModel(h, $"{threat.Name} — {path}", path, threat.PackageName));
                        added++;
                    }
                }
            }

            StatusMessage = added > 0
                ? $"{added} öğe tarama geçmişinden yüklendi"
                : "Yüklenecek yeni öğe yok";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task HashAllInstalledAsync(CancellationToken ct)
    {
        if (_adb.SelectedDevice is null)
        {
            StatusMessage = "Cihaz bağlı değil";
            return;
        }

        var ok = await _dialogs.ShowConfirmationAsync(
            "Tüm APK'ları hash'le",
            "Cihazdaki tüm kurulu uygulamaların APK dosyaları hash'lenecek.\n\n" +
            "• Yaklaşık 100–300 dosya\n" +
            "• Her hash bir VirusTotal sorgusu harcar\n" +
            "• Ücretsiz API: günlük ~450 istek\n\n" +
            "Devam edilsin mi?");
        if (!ok)
            return;

        IsBusy = true;
        ProgressPercent = 0;
        try
        {
            var raw = await _adb.ExecuteShellAsync("pm list packages -f", ct);
            var pathToPkg = new Dictionary<string, string>();
            var paths = new HashSet<string>();

            foreach (var line in raw.Split('\n'))
            {
                var t = line.Trim();
                if (!t.StartsWith("package:", StringComparison.Ordinal))
                    continue;

                var rest = t["package:".Length..];
                var eq = rest.IndexOf('=');
                var path = (eq > 0 ? rest[..eq] : rest).Trim();
                var pkg = eq > 0 ? rest[(eq + 1)..].Trim() : path;
                if (path.Length > 0)
                {
                    paths.Add(path);
                    pathToPkg[path] = pkg;
                }
            }

            var list = paths.OrderBy(p => p).ToList();
            var done = 0;

            for (var i = 0; i < list.Count; i += 20)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = list.Skip(i).Take(20).ToList();
                var cmd = string.Join(" ", chunk.Select(p => $"\"{p}\""));
                var outRaw = await _adb.ExecuteShellAsync($"md5sum {cmd} 2>/dev/null", ct);

                foreach (var hashLine in outRaw.Split('\n'))
                {
                    var hl = hashLine.Trim();
                    if (hl.Length < 34)
                        continue;

                    var hash = hl[..32];
                    var path = hl[33..].Trim();
                    if (hash.Length != 32 || !hash.All(Uri.IsHexDigit))
                        continue;
                    if (ContainsHash(hash))
                        continue;

                    var pkg = pathToPkg.TryGetValue(path, out var p) ? p : path;
                    Items.Add(new VtItemViewModel(hash, pkg, path, pkg));
                }

                done += chunk.Count;
                ProgressPercent = list.Count == 0 ? 0 : done * 100 / list.Count;
                ProgressText = $"{done}/{list.Count} hash hesaplandı";
            }

            StatusMessage = $"{Items.Count} hash hazır — Tümünü Doğrula ile VT'ye sorun";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "İşlem iptal edildi";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void AddManualHashes()
    {
        var tokens = ManualHashes
            .Split(['\n', ',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(h => h.Length is 32 or 40 or 64)
            .Select(h => h.ToLowerInvariant())
            .Distinct()
            .ToList();

        var added = 0;
        foreach (var hash in tokens)
        {
            if (ContainsHash(hash))
                continue;
            Items.Add(new VtItemViewModel(hash, $"Manuel: {hash[..Math.Min(12, hash.Length)]}…", null, null));
            added++;
        }

        StatusMessage = added > 0
            ? $"{added} hash eklendi"
            : "Geçerli hash bulunamadı (MD5/SHA1/SHA256)";
        if (added > 0)
            ManualHashes = "";
    }

    [RelayCommand]
    private async Task VerifyAllAsync(CancellationToken ct)
    {
        var targets = Items
            .Where(i => i.Report is null)
            .ToList();

        if (targets.Count == 0)
        {
            StatusMessage = "Doğrulanacak öğe yok";
            return;
        }

        IsBusy = true;
        var malicious = 0;
        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = targets[i];
                item.StatusText = "İşleniyor…";
                ProgressPercent = (i + 1) * 100 / targets.Count;
                ProgressText = $"Sorgu {i + 1}/{targets.Count}";

                try
                {
                    var report = await _vt.CheckHashAsync(item.Hash, ct);
                    ApplyReport(item, report);
                    if (report.Verdict == VtVerdict.Malicious)
                        malicious++;
                }
                catch (VtException ex) when (ex.IsQuota)
                {
                    item.StatusText = "Kota doldu";
                    StatusMessage = ex.Message;
                    break;
                }
                catch (VtException ex)
                {
                    item.StatusText = ex.Message;
                }
                catch (Exception ex)
                {
                    item.StatusText = ex.Message;
                    _logger.Debug(ex, "VT hash hatası");
                }
            }

            ProgressPercent = 100;
            Status = await _vt.GetStatusAsync();

            if (malicious > 0)
            {
                StatusMessage = $"{malicious} hash kötü amaçlı — kota: {Status?.QuotaText}";
                _notification.ShowError("VirusTotal",
                    $"{malicious} dosya/hash kötü amaçlı raporlandı.");
            }
            else
            {
                StatusMessage = $"Doğrulama tamam (kota: {Status?.QuotaText})";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Doğrulama iptal edildi";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task VerifyItemAsync(VtItemViewModel item, CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            item.StatusText = "İşleniyor…";
            var report = await _vt.CheckHashAsync(item.Hash, ct);
            ApplyReport(item, report);
            Status = await _vt.GetStatusAsync();
        }
        catch (VtException ex)
        {
            item.StatusText = ex.Message;
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UploadItemAsync(VtItemViewModel item, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(item.DevicePath))
        {
            StatusMessage = "Cihaz yolu yok — yükleme yapılamaz";
            return;
        }

        if (_adb.SelectedDevice is null)
        {
            StatusMessage = "Cihaz bağlı değil";
            return;
        }

        var ok = await _dialogs.ShowConfirmationAsync(
            "VirusTotal'e yükle",
            $"Dosya VirusTotal'e yüklenecek:\n{item.DevicePath}\n\n" +
            "• Dosya içeriği üçüncü tarafa gönderilir\n" +
            "• Kota harcanır, analiz birkaç dakika sürebilir\n\n" +
            "Devam edilsin mi?");
        if (!ok)
            return;

        IsBusy = true;
        var temp = Path.Combine(Path.GetTempPath(), $"vt_{Guid.NewGuid():N}.apk");
        try
        {
            item.StatusText = "Cihazdan çekiliyor…";
            await _sync.PullAsync(item.DevicePath, temp, cancellationToken: ct);

            var report = await _vt.UploadAndScanAsync(temp,
                new Progress<string>(s => StatusMessage = s), ct);

            if (report is not null)
            {
                ApplyReport(item, report);
                if (report.Verdict == VtVerdict.Malicious)
                    _notification.ShowError("VirusTotal",
                        $"{report.Malicious} motor '{item.Label}' için kötü amaçlı dedi");
            }

            Status = await _vt.GetStatusAsync();
        }
        catch (VtException ex)
        {
            item.StatusText = ex.Message;
            StatusMessage = ex.Message;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Yükleme hatası: {ex.Message}";
            _logger.Error(ex, "VT yükleme hatası");
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
                // geç
            }

            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenPermalink(VtItemViewModel item)
    {
        if (string.IsNullOrEmpty(item.Permalink))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(item.Permalink) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Link açılamadı: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportResultsAsync()
    {
        var withReport = Items.Where(i => i.Report is not null).ToList();
        if (withReport.Count == 0)
        {
            StatusMessage = "Dışa aktarılacak sonuç yok";
            return;
        }

        var path = await _dialogs.PickSaveFileAsync(
            "VirusTotal sonuçları",
            "JSON|*.json|CSV|*.csv",
            $"vt_sonuclari_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new StringBuilder();
                sb.AppendLine("hash,label,karar,kotu_amacli,supheli,temiz,algilanamadi,motor,oneri,permalink");
                foreach (var i in withReport)
                {
                    var r = i.Report!;
                    sb.AppendLine(string.Join(",",
                        r.RequestedHash, Csv(i.Label), r.Verdict,
                        r.Malicious, r.Suspicious, r.Harmless, r.Undetected,
                        r.TotalEngines, Csv(r.SuggestedLabel), r.Permalink));
                }

                await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8);
            }
            else
            {
                var payload = withReport.Select(i => new { i.Label, i.Hash, Report = i.Report });
                await File.WriteAllTextAsync(path,
                    JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            }

            StatusMessage = $"Dışa aktarıldı: {path}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Dışa aktarma hatası: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearItems() => Items.Clear();

    private static void ApplyReport(VtItemViewModel item, VtReport report)
    {
        item.Report = report;
        item.StatusText = report.Verdict switch
        {
            VtVerdict.Malicious => $"{report.Malicious} motor kötü amaçlı",
            VtVerdict.Suspicious => $"{report.Suspicious} motor şüpheli",
            VtVerdict.Clean => "Temiz",
            VtVerdict.Unknown => "VT DB'sinde yok",
            _ => "Hata"
        };
    }

    private bool ContainsHash(string hash) =>
        Items.Any(i => i.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase));

    private async Task<List<(string Hash, string Path)>> ComputeHashForThreatAsync(
        ThreatItem threat, CancellationToken ct)
    {
        var results = new List<(string, string)>();
        try
        {
            var paths = new List<string>();
            if (!string.IsNullOrEmpty(threat.FilePath))
                paths.Add(threat.FilePath);

            if (!string.IsNullOrEmpty(threat.PackageName))
            {
                var pm = await _adb.ExecuteShellAsync($"pm path {threat.PackageName}", ct);
                foreach (var line in pm.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.StartsWith("package:", StringComparison.Ordinal))
                        paths.Add(t["package:".Length..].Trim());
                }
            }

            foreach (var path in paths.Distinct())
            {
                var raw = await _adb.ExecuteShellAsync($"md5sum \"{path}\" 2>/dev/null", ct);
                var hash = raw.Split(' ')[0].Trim();
                if (hash.Length == 32 && hash.All(Uri.IsHexDigit))
                    results.Add((hash, path));
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Tehdit hash hesaplama: {Pkg}", threat.PackageName);
        }

        return results;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }

        return "";
    }

    private static string Csv(string s) =>
        $"\"{s.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}

public sealed partial class VtItemViewModel : ObservableObject
{
    public string Hash { get; }
    public string Label { get; }
    public string? DevicePath { get; }
    public string? PackageName { get; }

    [ObservableProperty] private VtReport? _report;
    [ObservableProperty] private string _statusText = "Bekliyor";

    public VtItemViewModel(string hash, string label, string? devicePath, string? packageName)
    {
        Hash = hash;
        Label = label;
        DevicePath = devicePath;
        PackageName = packageName;
    }

    public bool HasReport => Report is not null;

    public string VerdictText => Report?.VerdictText ?? "—";
    public string VerdictColor => Report?.VerdictColor ?? "#9E9E9E";
    public string SeverityText => Report?.SeverityText ?? "";
    public bool IsThreat => Report?.Verdict == VtVerdict.Malicious;

    public string EngineSummary => Report is null ? "" :
        $"{Report.Malicious} kötü / {Report.Suspicious} şüpheli / {Report.TotalEngines} motor";

    public string TopMatchesText => Report?.TopMatches.Count > 0
        ? string.Join(" • ", Report.TopMatches.Take(3).Select(m => $"{m.Engine} → {m.Result}"))
        : "";

    public string Permalink => Report?.Permalink ?? "";

    partial void OnReportChanged(VtReport? value)
    {
        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(VerdictText));
        OnPropertyChanged(nameof(VerdictColor));
        OnPropertyChanged(nameof(SeverityText));
        OnPropertyChanged(nameof(IsThreat));
        OnPropertyChanged(nameof(EngineSummary));
        OnPropertyChanged(nameof(TopMatchesText));
        OnPropertyChanged(nameof(Permalink));
    }
}
