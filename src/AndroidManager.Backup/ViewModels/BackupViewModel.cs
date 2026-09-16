using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using AndroidManager.Core;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AndroidManager.Backup.ViewModels;

public sealed partial class BackupViewModel : ObservableObject
{
    private readonly IBackupService _backup;
    private readonly IElevatedShellService _elevated;
    private readonly IAdbService _adb;
    private readonly IAppDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly INotificationService _notifications;
    private CancellationTokenSource? _cts;
    private bool _listeningDevice;

    [ObservableProperty] private bool _backupSms = true;
    [ObservableProperty] private bool _backupContacts = true;
    [ObservableProperty] private bool _backupPhotos = true;
    [ObservableProperty] private bool _backupVideos;
    [ObservableProperty] private bool _backupApps;
    [ObservableProperty] private bool _backupAppData;
    [ObservableProperty] private bool _compress = true;
    [ObservableProperty] private string _savePath = string.Empty;
    [ObservableProperty] private string _note = string.Empty;
    [ObservableProperty] private string _appDataHint = "Oyun kayıtları için root (Magisk) gerekir";
    [ObservableProperty] private bool _isRootAvailable;

    [ObservableProperty] private bool _restoreSms;
    [ObservableProperty] private bool _restoreContacts = true;
    [ObservableProperty] private bool _restorePhotos = true;
    [ObservableProperty] private bool _restoreApps;
    [ObservableProperty] private bool _restoreAppData;
    [ObservableProperty] private bool _overwriteApps;
    [ObservableProperty] private string? _selectedBackupFile;
    [ObservableProperty] private string _restoreDataHint = "Uygulama verisi için root (Magisk) gerekir";
    [ObservableProperty] private string _restoreCatalogStatus = "";
    [ObservableProperty] private string _restoreAppFilter = "";
    [ObservableProperty] private bool _hideSystemRestoreApps;
    [ObservableProperty] private bool _isInspectingBackup;
    [ObservableProperty] private bool _restoreCatalogLoaded;
    [ObservableProperty] private ObservableCollection<RestoreAppItemViewModel> _restoreAppItems = [];
    [ObservableProperty] private ICollectionView? _restoreAppsView;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private BackupProgress _progress = new();
    [ObservableProperty] private string _statusMessage = "Hazır";
    [ObservableProperty] private int _activeTab;

    [ObservableProperty] private ObservableCollection<BackupJobViewModel> _history = [];

    public BackupViewModel(
        IBackupService backup,
        IElevatedShellService elevated,
        IAdbService adb,
        IAppDialogService dialogs,
        IUiDispatcher dispatcher,
        ISettingsService settings,
        INotificationService notifications)
    {
        _backup = backup;
        _elevated = elevated;
        _adb = adb;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _notifications = notifications;
        SavePath = string.IsNullOrWhiteSpace(settings.Current.DefaultBackupPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "AndroidManager",
                "Backups")
            : settings.Current.DefaultBackupPath;
        Directory.CreateDirectory(SavePath);
    }

    public async Task InitializeAsync()
    {
        if (!_listeningDevice)
        {
            _listeningDevice = true;
            _adb.SelectedDeviceChanged += OnSelectedDeviceChanged;
        }

        await LoadHistoryAsync().ConfigureAwait(true);
        await RefreshRootHintAsync().ConfigureAwait(true);
    }

    private void OnSelectedDeviceChanged(object? sender, ConnectedDevice? device) =>
        _dispatcher.Observe(async () =>
        {
            try
            {
                await RefreshRootHintAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Backup root hint refresh failed after device change");
            }
        });

    private async Task RefreshRootHintAsync()
    {
        try
        {
            if (_adb.SelectedDevice is null)
            {
                IsRootAvailable = false;
                AppDataHint = "Önce bir cihaz seçin — oyun verisi için root gerekir";
                SetRestoreDataHint(null);
                return;
            }

            IsRootAvailable = await _elevated.IsAvailableAsync().ConfigureAwait(true);
            AppDataHint = IsRootAvailable
                ? "Root hazır — /data/data, Android/data ve OBB yedeklenir (oyun kaldığı yerden devam eder)"
                : "Root yok — Magisk'te Shell uygulamasına izin verin veya Root'u Etkinleştir deneyin";
            SetRestoreDataHint(_adb.SelectedDevice);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Root probe for backup failed");
            IsRootAvailable = false;
            AppDataHint = "Root kontrol edilemedi — cihaz bağlı ve seçili mi?";
            RestoreDataHint = AppDataHint;
        }
    }

    public int SelectedRestoreAppCount => RestoreAppItems.Count(i => i.IsSelected);
    public bool HasRestoreApps => RestoreAppItems.Count > 0;
    public string SelectedRestoreAppSummary
    {
        get
        {
            var selected = RestoreAppItems.Where(i => i.IsSelected).ToList();
            var apk = selected.Count(i => i.HasApk);
            var data = selected.Count(i => i.HasData);
            var system = selected.Count(i => i.IsLikelySystem);
            return selected.Count == 0
                ? "Uygulama seçilmedi"
                : $"{selected.Count} seçili — APK: {apk}, veri: {data}"
                  + (system > 0 ? $", sistem: {system}" : "");
        }
    }

    private void SetRestoreDataHint(ConnectedDevice? device)
    {
        if (device is null)
        {
            RestoreDataHint = "Önce bir cihaz seçin — uygulama verisi için root gerekir";
            return;
        }

        if (device.IsRecovery)
        {
            RestoreDataHint = IsRootAvailable
                ? "TWRP: APK kurulumu denenebilir. /data açık ve root varsa seçilen uygulamaların verisi de yüklenir."
                : "TWRP: APK kurulumu denenebilir. /data şifreliyse veya Magisk yoksa uygulama verisi yüklenmez.";
            return;
        }

        RestoreDataHint = IsRootAvailable
            ? "Root hazır — seçilen uygulamaların /data/data, Android/data ve OBB verisi yüklenebilir"
            : "Root yok — yalnızca APK kurulur; oyun kayıtları için Magisk gerekir";
    }

    partial void OnBackupAppDataChanged(bool value)
    {
        if (value)
            BackupApps = true;
    }

    partial void OnRestoreAppDataChanged(bool value)
    {
        if (value)
            RestoreApps = true;
    }

    partial void OnSelectedBackupFileChanged(string? value) =>
        ObservedTask.Run(LoadRestoreCatalogAsync(value));

    partial void OnRestoreAppFilterChanged(string value) => RestoreAppsView?.Refresh();

    partial void OnHideSystemRestoreAppsChanged(bool value) => RestoreAppsView?.Refresh();

    [RelayCommand]
    private async Task StartBackupAsync()
    {
        if (string.IsNullOrWhiteSpace(SavePath))
        {
            StatusMessage = "Kayıt klasörü seçin";
            return;
        }

        Directory.CreateDirectory(SavePath);
        _cts = new CancellationTokenSource();
        IsRunning = true;

        if (BackupAppData && !IsRootAvailable)
        {
            var proceed = await _dialogs.ShowConfirmationAsync(
                "Root gerekli",
                "Oyun/uygulama verisi için cihazda root (Magisk) ve su izni gerekir. " +
                "Yine de devam ederseniz bu kategori boş kalır; yalnızca APK alınabilir. Devam?");
            if (!proceed)
            {
                IsRunning = false;
                StatusMessage = "İptal edildi";
                return;
            }
        }

        var options = new BackupOptions
        {
            Type = BuildBackupType(),
            SavePath = SavePath,
            Note = Note,
            Compress = Compress
        };

        try
        {
            var job = await _backup.CreateBackupAsync(
                options,
                new Progress<BackupProgress>(p =>
                {
                    _dispatcher.Observe(() =>
                    {
                        Progress = p;
                        StatusMessage = p.CurrentStep;
                    });
                }),
                _cts.Token);

            if (job.Status == BackupStatus.Completed)
            {
                var detail = string.IsNullOrWhiteSpace(job.Note) ? "" : $" ({job.Note})";
                var partial = job.Note.Contains('!', StringComparison.Ordinal);
                StatusMessage = partial
                    ? $"Kısmi tamamlandı — {job.SizeFormatted}{detail}"
                    : $"Tamamlandı — {job.SizeFormatted}{detail}";
            }
            else
            {
                StatusMessage = job.Status.ToString();
            }

            _notifications.ShowBackupComplete(
                job.DeviceModel,
                job.SizeFormatted,
                job.Status == BackupStatus.Completed);

            await LoadHistoryAsync();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "İptal edildi";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Backup failed");
            StatusMessage = ex.Message;
            _notifications.ShowBackupComplete("Cihaz", "-", false);
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void CancelBackup()
    {
        _cts?.Cancel();
        StatusMessage = "İptal ediliyor...";
    }

    [RelayCommand]
    private async Task StartRestoreAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedBackupFile))
        {
            StatusMessage = "Yedek dosyası seçin";
            return;
        }

        var selectedPackages = RestoreAppItems
            .Where(i => i.IsSelected)
            .Select(i => i.PackageName)
            .ToList();
        var type = BuildRestoreType();
        if (RestoreCatalogLoaded && selectedPackages.Count == 0)
        {
            type &= ~BackupType.Apps;
            type &= ~BackupType.AppData;
        }

        if (type == BackupType.None)
        {
            StatusMessage = "Geri yüklenecek bir şey seçin (uygulama, rehber veya fotoğraf)";
            return;
        }

        var selectedItems = RestoreAppItems.Where(i => i.IsSelected).ToList();
        var systemCount = selectedItems.Count(i => i.IsLikelySystem);
        if (systemCount > 0)
        {
            var proceedSystem = await _dialogs.ShowConfirmationAsync(
                "Sistem uygulamaları",
                $"{systemCount} sistem paketi seçildi. Bunlar genellikle ROM ile gelir; üzerine yazmak başarısız olabilir. Devam?");
            if (!proceedSystem)
            {
                StatusMessage = "İptal edildi";
                return;
            }
        }

        if (type.HasFlag(BackupType.AppData) && !IsRootAvailable)
        {
            var proceedRoot = await _dialogs.ShowConfirmationAsync(
                "Root gerekli",
                "Seçilen uygulamaların verisi için Magisk/su gerekir. TWRP'de /data şifreliyse veri yüklenmez. " +
                "Yine de devam ederseniz yalnızca APK kurulmayı dener. Devam?");
            if (!proceedRoot)
            {
                StatusMessage = "İptal edildi";
                return;
            }
        }

        _cts = new CancellationTokenSource();
        IsRunning = true;

        try
        {
            var result = await _backup.RestoreBackupAsync(
                SelectedBackupFile,
                new RestoreOptions
                {
                    Type = type,
                    OverwriteApps = OverwriteApps,
                    PackageNames = RestoreCatalogLoaded ? selectedPackages : null
                },
                new Progress<BackupProgress>(p =>
                {
                    _dispatcher.Observe(() =>
                    {
                        Progress = p;
                        StatusMessage = p.CurrentStep;
                    });
                }),
                _cts.Token);

            StatusMessage = result.Success ? result.Message : $"Hata: {result.Message}";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private async Task BrowseSavePathAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Yedek kayıt klasörünü seçin");
        if (!string.IsNullOrWhiteSpace(folder))
            SavePath = folder;
    }

    [RelayCommand]
    private async Task BrowseBackupFileAsync()
    {
        var files = await _dialogs.PickOpenFilesAsync(
            "Geri yüklenecek yedeği seçin",
            "Backup Dosyaları (*.zip)|*.zip|Tüm Dosyalar|*.*",
            multiSelect: false);
        if (files is { Count: > 0 })
            SelectedBackupFile = files[0];
    }

    private async Task LoadRestoreCatalogAsync(string? path)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            ClearRestoreCatalog();
            if (string.IsNullOrWhiteSpace(path))
            {
                RestoreCatalogStatus = "";
                return;
            }

            IsInspectingBackup = true;
            RestoreCatalogStatus = "Yedek inceleniyor…";
        }).ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var entries = await _backup.InspectBackupAppsAsync(path).ConfigureAwait(true);
            await _dispatcher.InvokeAsync(() => ApplyRestoreCatalog(entries)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Backup inspect failed: {Path}", path);
            await _dispatcher.InvokeAsync(() =>
            {
                IsInspectingBackup = false;
                RestoreCatalogLoaded = false;
                RestoreCatalogStatus = "Arşiv okunamadı — tüm uygulamalar yüklenebilir";
            }).ConfigureAwait(true);
        }
    }

    private void ClearRestoreCatalog()
    {
        foreach (var item in RestoreAppItems)
            item.SelectionChanged -= OnRestoreAppSelectionChanged;
        RestoreAppItems.Clear();
        RestoreAppsView = null;
        RestoreCatalogLoaded = false;
        IsInspectingBackup = false;
        NotifyRestoreSelection();
    }

    private void ApplyRestoreCatalog(IReadOnlyList<BackupAppEntry> entries)
    {
        foreach (var item in RestoreAppItems)
            item.SelectionChanged -= OnRestoreAppSelectionChanged;

        var items = new ObservableCollection<RestoreAppItemViewModel>();
        foreach (var entry in entries)
        {
            var vm = new RestoreAppItemViewModel(entry)
            {
                IsSelected = !entry.IsLikelySystem && (entry.HasApk || entry.HasData)
            };
            vm.SelectionChanged += OnRestoreAppSelectionChanged;
            items.Add(vm);
        }

        RestoreAppItems = items;
        RestoreAppsView = CollectionViewSource.GetDefaultView(RestoreAppItems);
        if (RestoreAppsView is not null)
            RestoreAppsView.Filter = FilterRestoreApp;

        RestoreCatalogLoaded = true;
        IsInspectingBackup = false;

        var apk = entries.Count(e => e.HasApk);
        var data = entries.Count(e => e.HasData);
        var system = entries.Count(e => e.IsLikelySystem);
        RestoreCatalogStatus = entries.Count == 0
            ? "Bu yedekte uygulama (APK/veri) yok"
            : $"{entries.Count} uygulama — APK: {apk}, veri: {data}, sistem tahmini: {system}";

        if (items.Any(i => i.IsSelected))
            RestoreApps = true;

        NotifyRestoreSelection();
        OnPropertyChanged(nameof(HasRestoreApps));
    }

    private bool FilterRestoreApp(object obj)
    {
        if (obj is not RestoreAppItemViewModel item)
            return false;
        if (HideSystemRestoreApps && item.IsLikelySystem)
            return false;
        if (string.IsNullOrWhiteSpace(RestoreAppFilter))
            return true;
        return item.PackageName.Contains(RestoreAppFilter.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private void OnRestoreAppSelectionChanged() => NotifyRestoreSelection();

    private void NotifyRestoreSelection()
    {
        OnPropertyChanged(nameof(SelectedRestoreAppCount));
        OnPropertyChanged(nameof(SelectedRestoreAppSummary));
        OnPropertyChanged(nameof(HasRestoreApps));
    }

    [RelayCommand]
    private void SelectAllRestoreApps()
    {
        if (RestoreAppsView is null)
            return;
        foreach (var item in RestoreAppsView.Cast<RestoreAppItemViewModel>())
            item.IsSelected = true;
    }

    [RelayCommand]
    private void ClearRestoreAppSelection()
    {
        foreach (var item in RestoreAppItems)
            item.IsSelected = false;
    }

    [RelayCommand]
    private void SelectUserRestoreApps()
    {
        foreach (var item in RestoreAppItems)
            item.IsSelected = !item.IsLikelySystem && (item.HasApk || item.HasData);
    }

    [RelayCommand]
    private void SelectRestoreAppsWithData()
    {
        foreach (var item in RestoreAppItems)
            item.IsSelected = item.HasData;
        if (RestoreAppItems.Any(i => i.IsSelected))
            RestoreAppData = true;
    }

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        var jobs = await _backup.GetBackupHistoryAsync();
        History = new ObservableCollection<BackupJobViewModel>(jobs.Select(j => new BackupJobViewModel(j)));
    }

    [RelayCommand]
    private async Task DeleteHistoryItemAsync(BackupJobViewModel? item)
    {
        if (item is null) return;

        var ok = await _dialogs.ShowConfirmationAsync(
            "Sil",
            $"'{item.SavePath}' kaydı silinsin mi? Dosya silinmeyecek.");
        if (!ok) return;

        await _backup.DeleteBackupAsync(item.Id);
        History.Remove(item);
    }

    [RelayCommand]
    private void OpenBackupFolder(BackupJobViewModel? item)
    {
        if (item is null) return;
        var dir = Path.GetDirectoryName(item.SavePath);
        if (dir is not null && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }

    private BackupType BuildBackupType()
    {
        var type = BackupType.None;
        if (BackupSms) type |= BackupType.Sms;
        if (BackupContacts) type |= BackupType.Contacts;
        if (BackupPhotos) type |= BackupType.Photos;
        if (BackupVideos) type |= BackupType.Videos;
        if (BackupApps) type |= BackupType.Apps;
        if (BackupAppData) type |= BackupType.AppData;
        return type;
    }

    private BackupType BuildRestoreType()
    {
        var type = BackupType.None;
        if (RestoreSms) type |= BackupType.Sms;
        if (RestoreContacts) type |= BackupType.Contacts;
        if (RestorePhotos) type |= BackupType.Photos;
        if (RestoreApps) type |= BackupType.Apps;
        if (RestoreAppData) type |= BackupType.AppData;
        return type;
    }
}

public sealed class BackupJobViewModel
{
    public int Id { get; }
    public string DeviceModel { get; }
    public BackupType Type { get; }
    public string SavePath { get; }
    public string CreatedAt { get; }
    public string Size { get; }
    public BackupStatus Status { get; }
    public string Note { get; }
    public string TypeLabel { get; }
    public string StatusIcon { get; }

    public BackupJobViewModel(BackupJob job)
    {
        Id = job.Id;
        DeviceModel = job.DeviceModel;
        Type = job.Type;
        SavePath = job.SavePath;
        CreatedAt = job.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        Size = job.SizeFormatted;
        Status = job.Status;
        Note = job.Note;
        TypeLabel = BuildTypeLabel(job.Type);
        StatusIcon = job.Status switch
        {
            BackupStatus.Completed => "OK",
            BackupStatus.Failed => "ERR",
            BackupStatus.Cancelled => "IPT",
            BackupStatus.Running => "...",
            _ => "-"
        };
    }

    private static string BuildTypeLabel(BackupType type)
    {
        var parts = new List<string>();
        if (type.HasFlag(BackupType.Sms)) parts.Add("SMS");
        if (type.HasFlag(BackupType.Contacts)) parts.Add("Rehber");
        if (type.HasFlag(BackupType.Photos)) parts.Add("Foto");
        if (type.HasFlag(BackupType.Videos)) parts.Add("Video");
        if (type.HasFlag(BackupType.Apps)) parts.Add("APK");
        if (type.HasFlag(BackupType.AppData)) parts.Add("Veri");
        return string.Join(" + ", parts);
    }
}

public sealed partial class RestoreAppItemViewModel : ObservableObject
{
    public RestoreAppItemViewModel(BackupAppEntry entry) => Entry = entry;

    public BackupAppEntry Entry { get; }
    public string PackageName => Entry.PackageName;
    public bool HasApk => Entry.HasApk;
    public bool HasData => Entry.HasData;
    public bool IsLikelySystem => Entry.IsLikelySystem;
    public string SizeLabel => Entry.SizeFormatted;

    public string ContentsLabel
    {
        get
        {
            var parts = new List<string>();
            if (HasApk) parts.Add("APK");
            if (HasData) parts.Add("Veri");
            if (IsLikelySystem) parts.Add("Sistem");
            return string.Join(" · ", parts);
        }
    }

    [ObservableProperty] private bool _isSelected;

    public event Action? SelectionChanged;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isNull = value is null || (value is string s && string.IsNullOrWhiteSpace(s));
        var visible = Invert ? !isNull : isNull;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
