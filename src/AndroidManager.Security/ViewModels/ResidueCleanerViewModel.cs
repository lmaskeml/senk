using System.Collections.ObjectModel;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AndroidManager.Security.ViewModels;

public sealed partial class ResidueCleanerViewModel : ObservableObject
{
    private readonly IResidueCleanerService _cleaner;
    private readonly IAppDialogService _dialogs;
    private readonly INotificationService _notification;
    private readonly ILogger _logger;
    private CancellationTokenSource? _scanCts;

    [ObservableProperty] private ObservableCollection<ResidueItemViewModel> _items = [];
    [ObservableProperty] private ObservableCollection<string> _whitelist = [];
    [ObservableProperty] private string _whitelistInput = "";
    [ObservableProperty] private string _statusMessage = "Kalıntı taraması için cihaz bağlayın";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private bool _rootAvailable;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isCleaning;
    [ObservableProperty] private int _scanPercent;
    [ObservableProperty] private string _scanPhase = "";

    [ObservableProperty] private bool _scanOrphaned = true;
    [ObservableProperty] private bool _scanApks = true;
    [ObservableProperty] private bool _scanCaches = true;
    [ObservableProperty] private bool _scanTemp = true;
    [ObservableProperty] private bool _scanLogs = true;
    [ObservableProperty] private bool _scanEmpty = true;
    [ObservableProperty] private bool _scanThumbs = true;
    [ObservableProperty] private bool _scanDownload = true;
    [ObservableProperty] private bool _deepScan;

    public int SelectedCount => Items.Count(i => i.IsSelected);
    public long SelectedSize => Items.Where(i => i.IsSelected).Sum(i => i.Model.SizeBytes);
    public string SelectedSizeText => SizeFormatter.Format(SelectedSize);
    public bool HasItems => Items.Count > 0;

    public ResidueCleanerViewModel(
        IResidueCleanerService cleaner,
        IAppDialogService dialogs,
        INotificationService notification)
    {
        _cleaner = cleaner;
        _dialogs = dialogs;
        _notification = notification;
        _logger = Log.ForContext<ResidueCleanerViewModel>();
    }

    public async Task InitializeAsync()
    {
        RootAvailable = await _cleaner.RootAvailableAsync();
        Whitelist = new ObservableCollection<string>(await _cleaner.GetWhitelistAsync());
        StatusMessage = RootAvailable
            ? "Hazır — root ile /data altına erişilebilir"
            : "Root yok — sdcard ve PC kalıntıları taranır";
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        IsScanning = true;
        Items.Clear();
        NotifySelectionStats();
        SummaryText = "";
        StatusMessage = "Taranıyor…";

        try
        {
            var options = new ResidueScanOptions
            {
                ScanOrphanedData = ScanOrphaned,
                ScanLeftoverApks = ScanApks,
                ScanAppCaches = ScanCaches,
                ScanTempFiles = ScanTemp,
                ScanLogFiles = ScanLogs,
                ScanEmptyDirs = ScanEmpty,
                ScanThumbnails = ScanThumbs,
                ScanDownloadJunk = ScanDownload,
                DeepScan = DeepScan,
                WhitelistedPackages = Whitelist.ToList()
            };

            var result = await _cleaner.ScanAsync(options,
                new Progress<ScanProgress>(p =>
                {
                    ScanPhase = p.Phase;
                    ScanPercent = p.Percentage;
                }), ct);

            RootAvailable = result.RootAvailable;
            foreach (var item in result.Items)
            {
                var vm = new ResidueItemViewModel(item);
                vm.SelectionChanged += OnItemSelectionChanged;
                Items.Add(vm);
            }

            SummaryText = result.Summary;
            StatusMessage = result.RootActive
                ? result.Summary
                : result.Summary + (result.RootAvailable
                    ? " (root izni verilmedi — derin alanlar atlandı)"
                    : " (root yok — derin alanlar atlandı)");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Tarama iptal edildi";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("Kalıntı taraması", ex.Message);
            _logger.Error(ex, "Residue scan failed");
        }
        finally
        {
            IsScanning = false;
            NotifySelectionStats();
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    [RelayCommand]
    private async Task CleanSelectedAsync()
    {
        var selected = Items.Where(i => i.IsSelected).Select(i => i.Model).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "Temizlenecek öğe seçin";
            return;
        }

        var ok = await _dialogs.ShowConfirmationAsync(
            "Kalıntı temizle",
            $"{selected.Count} öğe ({SizeFormatter.Format(selected.Sum(i => i.SizeBytes))}) kalıcı olarak silinecek.\n\n" +
            "Geri dönüşü yok. Devam edilsin mi?");
        if (!ok)
            return;

        IsCleaning = true;
        try
        {
            var results = await _cleaner.CleanAsync(selected,
                new Progress<CleanProgress>(p =>
                {
                    StatusMessage = $"Temizleniyor ({p.Done}/{p.Total}): {p.CurrentThreat}";
                }));

            var success = results.Count(r => r.Success);
            var fail = results.Count - success;
            StatusMessage = $"Temizlik: {success} başarılı, {fail} başarısız";

            if (fail == 0)
                _notification.ShowInfo("Kalıntı temizleme", $"{success} öğe silindi");
            else
                _notification.ShowError("Kalıntı temizleme", $"{fail} öğe silinemedi");

            var failedIds = results.Where(r => !r.Success).Select(r => r.Item.Id).ToHashSet();
            for (var i = Items.Count - 1; i >= 0; i--)
            {
                var item = Items[i];
                if (item.IsSelected && !failedIds.Contains(item.Model.Id))
                {
                    item.SelectionChanged -= OnItemSelectionChanged;
                    Items.RemoveAt(i);
                }
            }

            NotifySelectionStats();
            SummaryText = Items.Count == 0
                ? "Liste temizlendi"
                : $"{Items.Count} kalıntı • {SizeFormatter.Format(Items.Sum(x => x.Model.SizeBytes))}";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("Temizleme", ex.Message);
        }
        finally
        {
            IsCleaning = false;
        }
    }

    [RelayCommand]
    private async Task CleanItemAsync(ResidueItemViewModel item)
    {
        var ok = await _dialogs.ShowConfirmationAsync(
            "Sil",
            $"{item.Model.Path}\n\nKalıcı olarak silinsin mi?");
        if (!ok)
            return;

        IsCleaning = true;
        try
        {
            var result = await _cleaner.CleanItemAsync(item.Model);
            if (result.Success)
            {
                item.SelectionChanged -= OnItemSelectionChanged;
                Items.Remove(item);
                NotifySelectionStats();
                StatusMessage = "Silindi: " + item.Model.Name;
            }
            else
            {
                StatusMessage = result.Message;
                await _dialogs.ShowMessageAsync("Silme", result.Message);
            }
        }
        finally
        {
            IsCleaning = false;
        }
    }

    [RelayCommand]
    private async Task TrimCachesAsync()
    {
        IsCleaning = true;
        try
        {
            var freed = await _cleaner.TrimSystemCachesAsync();
            StatusMessage = $"Sistem önbelleği temizlendi (~{SizeFormatter.Format(freed)})";
            _notification.ShowInfo("Önbellek", StatusMessage);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("Önbellek", ex.Message);
        }
        finally
        {
            IsCleaning = false;
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Items)
            item.IsSelected = true;
        NotifySelectionStats();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var item in Items)
            item.IsSelected = false;
        NotifySelectionStats();
    }

    [RelayCommand]
    private async Task AddWhitelistAsync()
    {
        var pkg = WhitelistInput.Trim();
        if (string.IsNullOrWhiteSpace(pkg))
            return;

        if (await _cleaner.AddWhitelistAsync(pkg))
        {
            if (!Whitelist.Contains(pkg, StringComparer.OrdinalIgnoreCase))
                Whitelist.Add(pkg);
            WhitelistInput = "";
            StatusMessage = $"Beyaz listeye eklendi: {pkg}";
        }
    }

    [RelayCommand]
    private async Task RemoveWhitelistAsync(string packageName)
    {
        if (await _cleaner.RemoveWhitelistAsync(packageName))
            Whitelist.Remove(packageName);
    }

    private void OnItemSelectionChanged() => NotifySelectionStats();

    private void NotifySelectionStats()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedSize));
        OnPropertyChanged(nameof(SelectedSizeText));
        OnPropertyChanged(nameof(HasItems));
    }
}

public sealed partial class ResidueItemViewModel : ObservableObject
{
    public ResidueItem Model { get; }
    public event Action? SelectionChanged;

    [ObservableProperty] private bool _isSelected;

    public ResidueItemViewModel(ResidueItem model) => Model = model;

    public string Name => Model.Name;
    public string Path => Model.Path;
    public string CategoryLabel => Model.CategoryLabel;
    public string SizeLabel => SizeFormatter.Format(Model.SizeBytes);
    public string Description => Model.Description;
    public bool RequiresRoot => Model.RequiresRoot;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();
}
