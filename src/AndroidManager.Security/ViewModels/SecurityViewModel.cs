using System.Collections.ObjectModel;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AndroidManager.Security.ViewModels;

public sealed partial class SecurityViewModel : ObservableObject, IDisposable
{
    private readonly ISecurityService _security;
    private readonly INotificationService _notification;
    private readonly IAppDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAdbService _adb;
    private CancellationTokenSource? _scanCts;
    private bool _disposed;

    [ObservableProperty] private ObservableCollection<ThreatViewModel> _threats = [];
    [ObservableProperty] private ObservableCollection<ThreatViewModel> _riskFactors = [];
    [ObservableProperty] private ObservableCollection<ThreatViewModel> _quarantine = [];
    [ObservableProperty] private ObservableCollection<ScanHistoryEntry> _history = [];
    [ObservableProperty] private SecurityStatus? _status;

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isCleaning;
    [ObservableProperty] private ScanProgress _scanProgress = new();
    [ObservableProperty] private CleanProgress _cleanProgress = new();
    [ObservableProperty] private string _statusMessage = "Hazır";
    [ObservableProperty] private int _selectedTab;

    [ObservableProperty] private bool _scanApps = true;
    [ObservableProperty] private bool _scanFiles = true;
    [ObservableProperty] private bool _scanNetwork = true;
    [ObservableProperty] private bool _scanPrivileges = true;
    [ObservableProperty] private bool _scanPersistence = true;
    [ObservableProperty] private bool _scanIntegrity = true;
    [ObservableProperty] private bool _deepScan;

    [ObservableProperty] private string _scoreText = "—";
    [ObservableProperty] private string _riskText = "Tarama yapılmadı";
    [ObservableProperty] private string _confidenceText = "";

    public SecurityViewModel(
        ISecurityService security,
        INotificationService notification,
        IAppDialogService dialogs,
        IUiDispatcher dispatcher,
        IAdbService adb)
    {
        _security = security;
        _notification = notification;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _adb = adb;
        _adb.DeviceConnectionChanged += OnDeviceConnectionChanged;
    }

    private void OnDeviceConnectionChanged(object? sender, Core.Events.DeviceConnectionChangedEventArgs e)
    {
        if (e.IsConnected || !IsScanning)
            return;

        _scanCts?.Cancel();
        _dispatcher.Observe(() => StatusMessage = "ADB bağlantısı kesildi — tarama iptal edildi");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _adb.DeviceConnectionChanged -= OnDeviceConnectionChanged;
        _scanCts?.Cancel();
        _scanCts?.Dispose();
    }

    public Task InitializeAsync() => RefreshStatusAsync();

    [RelayCommand]
    private async Task QuickScanAsync()
    {
        if (_adb.SelectedDevice is null)
        {
            StatusMessage = "Önce bir cihaz bağlayın";
            return;
        }

        await RunScanAsync(ct => _security.StartQuickScanAsync(
            new Progress<ScanProgress>(OnScanProgress), ct));
    }

    [RelayCommand]
    private async Task FullScanAsync()
    {
        if (_adb.SelectedDevice is null)
        {
            StatusMessage = "Önce bir cihaz bağlayın";
            return;
        }

        var options = new ScanOptions
        {
            ScanApps = ScanApps,
            ScanFiles = ScanFiles,
            ScanNetwork = ScanNetwork,
            ScanPrivileges = ScanPrivileges,
            ScanPersistence = ScanPersistence,
            ScanIntegrity = ScanIntegrity,
            DeepScan = DeepScan,
            ScanPermissions = true
        };

        await RunScanAsync(ct => _security.StartFullScanAsync(
            options, new Progress<ScanProgress>(OnScanProgress), ct));
    }

    [RelayCommand]
    private async Task RescueScanAsync()
    {
        if (_adb.SelectedDevice is null)
        {
            StatusMessage = "Önce bir cihaz bağlayın";
            return;
        }

        var ok = await _dialogs.ShowConfirmationAsync(
            "Kurtarma Modu",
            "Kurtarma taraması kalıcılık, ayrıcalık ve sideload paketlere odaklanır. Devam edilsin mi?");
        if (!ok) return;

        await RunScanAsync(ct => _security.StartRescueScanAsync(
            new Progress<ScanProgress>(OnScanProgress), ct));
    }

    private void OnScanProgress(ScanProgress p)
    {
        _dispatcher.Observe(() => ScanProgress = p);
    }

    private async Task RunScanAsync(Func<CancellationToken, Task<ScanSession>> scanFunc)
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        Threats.Clear();
        RiskFactors.Clear();
        StatusMessage = "Tarama başlatılıyor…";

        try
        {
            var session = await scanFunc(_scanCts.Token);
            if (session.Status == ScanStatus.Cancelled)
            {
                StatusMessage = "Tarama iptal edildi";
                return;
            }

            Threats = new ObservableCollection<ThreatViewModel>(
                session.Threats.Select(t => new ThreatViewModel(t)));
            RiskFactors = new ObservableCollection<ThreatViewModel>(
                session.RiskFactors.Select(t => new ThreatViewModel(t)));

            ApplyScore(session.Score);
            StatusMessage = session.IsClean
                ? $"Bilinen tehdit yok ({session.Duration:mm\\:ss})"
                : $"{session.Threats.Count} tehdit bulundu ({session.Duration:mm\\:ss})";

            if (!session.IsClean)
            {
                _notification.ShowInfo(
                    "Güvenlik taraması",
                    $"{session.CriticalCount} kritik, {session.HighCount} yüksek tehdit");
                SelectedTab = 1;
            }

            await RefreshStatusAsync();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Tarama iptal edildi";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Tarama hatası: {ex.Message}";
            Log.Error(ex, "Güvenlik taraması hatası");
            await _dialogs.ShowMessageAsync("Güvenlik", ex.Message);
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void ApplyScore(SecurityScoreCard? score)
    {
        if (score is null)
        {
            ScoreText = "—";
            RiskText = "Tarama yapılmadı";
            ConfidenceText = "";
            return;
        }

        ScoreText = score.Score.ToString();
        RiskText = score.RiskLevel switch
        {
            SecurityRiskLevel.Low => "Risk: Düşük",
            SecurityRiskLevel.Medium => "Risk: Orta",
            SecurityRiskLevel.High => "Risk: Yüksek",
            _ => "Risk: Kritik"
        };
        ConfidenceText = $"Güven: {score.ConfidenceLabel}";
    }

    [RelayCommand]
    private void CancelScan()
    {
        _scanCts?.Cancel();
        StatusMessage = "İptal ediliyor…";
    }

    [RelayCommand]
    private async Task CleanSelectedAsync()
    {
        var toClean = Threats.Where(t => t.IsSelected).Select(t => t.Model).ToList();
        if (toClean.Count == 0)
        {
            StatusMessage = "Temizlenecek tehdit seçin";
            return;
        }

        var confirm = await _dialogs.ShowConfirmationAsync(
            "Tehditleri temizle",
            $"{toClean.Count} tehdit temizlenecek. Bu işlem geri alınamayabilir. Devam edilsin mi?");
        if (!confirm) return;

        IsCleaning = true;
        try
        {
            var results = await _security.CleanAllThreatsAsync(
                toClean,
                new Progress<CleanProgress>(p => _dispatcher.Observe(() => CleanProgress = p)));

            var cleaned = results.Where(r => r.Success).Select(r => r.Threat.Id).ToHashSet();
            foreach (var t in Threats.Where(t => cleaned.Contains(t.Model.Id)).ToList())
                Threats.Remove(t);

            var success = results.Count(r => r.Success);
            var fail = results.Count(r => !r.Success);
            StatusMessage = $"{success} temizlendi" + (fail > 0 ? $", {fail} başarısız" : "");
            _notification.ShowInfo("Temizleme", $"{success} tehdit temizlendi");
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Temizleme hatası: {ex.Message}";
        }
        finally
        {
            IsCleaning = false;
        }
    }

    [RelayCommand]
    private async Task CleanAllAsync()
    {
        foreach (var t in Threats) t.IsSelected = true;
        await CleanSelectedAsync();
    }

    [RelayCommand]
    private async Task QuarantineSelectedAsync()
    {
        var toQ = Threats.Where(t => t.IsSelected).ToList();
        if (toQ.Count == 0) return;

        var confirm = await _dialogs.ShowConfirmationAsync(
            "Karantina",
            $"{toQ.Count} öğe Windows karantinasına alınacak ve cihazda devre dışı bırakılacak. Devam?");
        if (!confirm) return;

        var okCount = 0;
        foreach (var t in toQ.ToList())
        {
            if (!await _security.QuarantineAsync(t.Model)) continue;
            t.IsQuarantined = true;
            Threats.Remove(t);
            Quarantine.Add(t);
            okCount++;
        }

        StatusMessage = $"{okCount} öğe karantinaya alındı";
        await RefreshStatusAsync();
    }

    [RelayCommand]
    private async Task RestoreFromQuarantineAsync(ThreatViewModel? item)
    {
        if (item is null) return;
        var ok = await _security.RestoreFromQuarantineAsync(item.Model);
        if (ok)
        {
            Quarantine.Remove(item);
            StatusMessage = $"{item.Name} geri yüklendi";
        }
        else
        {
            StatusMessage = "Geri yükleme engellendi veya başarısız (yeniden tarama)";
            await _dialogs.ShowMessageAsync(
                "Karantina",
                "Geri yükleme engellendi: tehdit tanımları hâlâ eşleşiyor veya işlem başarısız.");
        }

        await RefreshStatusAsync();
    }

    [RelayCommand]
    private async Task UpdateDefinitionsAsync()
    {
        StatusMessage = "Tanımlar güncelleniyor…";
        var ok = await _security.UpdateDefinitionsAsync(new Progress<int>(p =>
            StatusMessage = $"Güncelleniyor… %{p}"));
        StatusMessage = ok ? "Tanımlar yenilendi" : "Güncelleme başarısız";
        if (ok) await RefreshStatusAsync();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var t in Threats) t.IsSelected = true;
    }

    [RelayCommand]
    private void SelectCritical()
    {
        foreach (var t in Threats)
            t.IsSelected = t.Model.Severity >= ThreatSeverity.High;
    }

    [RelayCommand]
    private async Task RefreshStatusAsync()
    {
        Status = await _security.GetSecurityStatusAsync();
        ApplyScore(Status.Score);
        var qList = await _security.GetQuarantineListAsync();
        Quarantine = new ObservableCollection<ThreatViewModel>(qList.Select(t => new ThreatViewModel(t)));
        var hist = await _security.GetScanHistoryAsync();
        History = new ObservableCollection<ScanHistoryEntry>(hist);
    }
}

public sealed partial class ThreatViewModel : ObservableObject
{
    public ThreatItem Model { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isQuarantined;

    public string Name => Model.Name;
    public string PackageName => Model.PackageName;
    public string FilePath => Model.FilePath;
    public string SeverityLabel => Model.SeverityLabel;
    public string SeverityColor => Model.SeverityColor;
    public string TypeLabel => Model.TypeLabel;
    public string Description => Model.Description;
    public string DetectedAt => Model.DetectedAt.ToString("dd.MM.yyyy HH:mm");
    public string Advice => Model.RemediationAdvice;
    public bool HasPackage => !string.IsNullOrEmpty(Model.PackageName);
    public bool HasFile => !string.IsNullOrEmpty(Model.FilePath);
    public string ScoreLabel => $"Risk {Model.RiskScore} · Güven %{Model.ConfidenceScore}";
    public string EvidenceText => Model.Evidence.Count == 0
        ? ""
        : string.Join(" · ", Model.Evidence.Select(e => e.Description).Take(4));

    public ThreatViewModel(ThreatItem model)
    {
        Model = model;
        IsQuarantined = model.IsQuarantined;
    }
}
