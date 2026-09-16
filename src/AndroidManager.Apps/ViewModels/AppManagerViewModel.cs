using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using AndroidManager.Core;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Prism.Navigation;
using Prism.Navigation.Regions;
using Serilog;

namespace AndroidManager.Apps.ViewModels;

public sealed partial class AppManagerViewModel : ObservableObject, INavigationAware
{
    private readonly IAppService _appService;
    private readonly IDebloaterService _debloater;
    private readonly IAppDialogService _dialogs;
    private List<AppItemViewModel> _allApps = [];

    [ObservableProperty] private ObservableCollection<AppItemViewModel> _apps = [];
    [ObservableProperty] private AppItemViewModel? _selectedApp;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _includeSystem;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _statusMessage = "Hazır";
    [ObservableProperty] private int _installProgress;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private int _selectedTabIndex;

    [ObservableProperty] private ObservableCollection<DebloatCandidate> _debloatCandidates = [];
    [ObservableProperty] private DebloatCandidate? _selectedDebloat;
    [ObservableProperty] private bool _isDebloatLoading;
    [ObservableProperty] private string _debloatSearchText = string.Empty;
    [ObservableProperty] private string _debloatStatusMessage = "DeBloater adayları yüklenmedi";
    [ObservableProperty] private string _debloatCategory = "Tümü";
    [ObservableProperty] private bool _showDisabledOnly;
    private List<DebloatCandidate> _allDebloat = [];

    public ObservableCollection<string> DebloatCategories { get; } = ["Tümü"];

    public AppManagerViewModel(
        IAppService appService,
        IDebloaterService debloater,
        IAppDialogService dialogs)
    {
        _appService = appService;
        _debloater = debloater;
        _dialogs = dialogs;
    }

    public void OnNavigatedTo(NavigationContext navigationContext)
    {
        if (navigationContext.Parameters.TryGetValue("tab", out string? tab)
            && string.Equals(tab, "debloat", StringComparison.OrdinalIgnoreCase))
        {
            SelectedTabIndex = 1;
        }
    }

    public bool IsNavigationTarget(NavigationContext navigationContext) => true;

    public void OnNavigatedFrom(NavigationContext navigationContext)
    {
    }

    public async Task InitializeAsync()
    {
        await LoadAppsAsync();
        await LoadDebloatAsync();
    }

    [RelayCommand]
    private async Task LoadAppsAsync()
    {
        IsLoading = true;
        StatusMessage = "Uygulamalar yükleniyor...";
        try
        {
            var apps = await _appService.GetInstalledAppsAsync(IncludeSystem);
            _allApps = apps.Select(a => new AppItemViewModel(a)).ToList();
            ApplyFilter();
            StatusMessage = $"{_allApps.Count} uygulama";

            // Lazy-load icons in background (first page)
            ObservedTask.Run(LoadIconsAsync(_allApps.Take(30)));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Uygulama listesi yükleme hatası");
            StatusMessage = $"Hata: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoadIconAsync(AppItemViewModel? item)
    {
        if (item is null || item.HasIcon)
            return;

        try
        {
            var bytes = await _appService.GetAppIconAsync(item.PackageName);
            if (bytes is null) return;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();

            item.Icon = bmp;
            item.HasIcon = true;
        }
        catch
        {
            // Keep letter avatar.
        }
    }

    [RelayCommand]
    private async Task InstallApkAsync()
    {
        var files = await _dialogs.PickOpenFilesAsync(
            "APK / Split paket seç",
            AndroidPackageFormats.OpenFileFilter,
            multiSelect: true);
        if (files is null || files.Count == 0)
            return;

        foreach (var file in files)
        {
            IsInstalling = true;
            InstallProgress = 0;
            StatusMessage = $"Yükleniyor: {Path.GetFileName(file)}";

            try
            {
                var result = await _appService.InstallApkAsync(
                    file,
                    new Progress<int>(p => InstallProgress = p));

                StatusMessage = result.Success
                    ? $"{Path.GetFileName(file)} yüklendi"
                    : $"Hata: {result.Message}";

                if (result.Success)
                    await LoadAppsAsync();
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }
            finally
            {
                IsInstalling = false;
            }
        }
    }

    [RelayCommand]
    private async Task UninstallAsync()
    {
        if (SelectedApp is null) return;

        var ok = await _dialogs.ShowConfirmationAsync(
            "Kaldır",
            $"'{SelectedApp.AppName}' kaldırılsın mı?");
        if (!ok) return;

        StatusMessage = $"Kaldırılıyor: {SelectedApp.AppName}";
        var success = await _appService.UninstallAsync(SelectedApp.PackageName);
        StatusMessage = success
            ? $"{SelectedApp.AppName} kaldırıldı"
            : "Kaldırma başarısız";

        if (success)
            await LoadAppsAsync();
    }

    [RelayCommand]
    private async Task ExtractApkAsync()
    {
        if (SelectedApp is null) return;

        var folder = await _dialogs.PickFolderAsync("APK kayıt klasörünü seçin");
        if (string.IsNullOrWhiteSpace(folder)) return;

        StatusMessage = $"APK çıkartılıyor: {SelectedApp.PackageName}";
        try
        {
            var path = await _appService.ExtractApkAsync(SelectedApp.PackageName, folder);
            StatusMessage = $"Kaydedildi: {path}";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ForceStopAsync()
    {
        if (SelectedApp is null) return;
        await _appService.ForceStopAsync(SelectedApp.PackageName);
        StatusMessage = $"{SelectedApp.AppName} durduruldu";
    }

    [RelayCommand]
    private async Task ClearDataAsync()
    {
        if (SelectedApp is null) return;

        var ok = await _dialogs.ShowConfirmationAsync(
            "Uyarı",
            $"'{SelectedApp.AppName}' verileri temizlensin mi? Bu işlem geri alınamaz.");
        if (!ok) return;

        await _appService.ClearDataAsync(SelectedApp.PackageName);
        StatusMessage = $"{SelectedApp.AppName} verileri temizlendi";
    }

    [RelayCommand]
    private async Task LoadDebloatAsync()
    {
        IsDebloatLoading = true;
        DebloatStatusMessage = "DeBloater adayları yükleniyor...";
        try
        {
            var categories = await _debloater.GetCategoriesAsync();
            DebloatCategories.Clear();
            DebloatCategories.Add("Tümü");
            foreach (var c in categories)
                DebloatCategories.Add(c.Name);

            _allDebloat = ShowDisabledOnly
                ? (await _debloater.GetDisabledAppsAsync()).ToList()
                : (await _debloater.GetDebloatableAppsAsync()).ToList();

            ApplyDebloatFilter();
            DebloatStatusMessage = $"{_allDebloat.Count} aday";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Debloat list failed");
            DebloatStatusMessage = $"Hata: {ex.Message}";
        }
        finally
        {
            IsDebloatLoading = false;
        }
    }

    [RelayCommand]
    private async Task DisableDebloatAsync()
    {
        if (SelectedDebloat is null) return;

        var ok = await _dialogs.ShowConfirmationAsync(
            "DeBloater onayı",
            $"'{SelectedDebloat.AppName}' ({SelectedDebloat.PackageName}) kullanıcı 0 için kaldırılsın mı?\n\n" +
            $"Risk: {SelectedDebloat.RiskLabel}\n" +
            "Komut: pm uninstall -k --user 0\n" +
            "Veriler korunur; Etkinleştir ile çoğu cihazda geri getirilebilir.");
        if (!ok) return;

        DebloatStatusMessage = $"Kaldırılıyor: {SelectedDebloat.PackageName}";
        var result = await _debloater.UninstallForUserAsync(SelectedDebloat.PackageName);
        DebloatStatusMessage = result.Message;
        if (result.Success)
            await LoadDebloatAsync();
        else
            await _dialogs.ShowMessageAsync("DeBloater", result.Message);
    }

    [RelayCommand]
    private async Task DisableUserDebloatAsync()
    {
        if (SelectedDebloat is null) return;

        var ok = await _dialogs.ShowConfirmationAsync(
            "Devre dışı bırak",
            $"'{SelectedDebloat.AppName}' pm disable-user ile pasifleştirilsin mi?\n\nRisk: {SelectedDebloat.RiskLabel}");
        if (!ok) return;

        DebloatStatusMessage = $"Devre dışı: {SelectedDebloat.PackageName}";
        var result = await _debloater.DisableAppAsync(SelectedDebloat.PackageName);
        DebloatStatusMessage = result.Message;
        if (result.Success)
            await LoadDebloatAsync();
        else
            await _dialogs.ShowMessageAsync("DeBloater", result.Message);
    }

    [RelayCommand]
    private async Task RestoreDebloatAsync()
    {
        if (SelectedDebloat is null) return;

        var ok = await _dialogs.ShowConfirmationAsync(
            "Etkinleştir / geri yükle",
            $"'{SelectedDebloat.AppName}' kullanıcı 0 için geri yüklensin / etkinleştirilsin mi?");
        if (!ok) return;

        DebloatStatusMessage = $"Geri yükleniyor: {SelectedDebloat.PackageName}";
        var result = await _debloater.EnableAppAsync(SelectedDebloat.PackageName);
        DebloatStatusMessage = result.Message;
        if (result.Success)
            await LoadDebloatAsync();
        else
            await _dialogs.ShowMessageAsync("DeBloater", result.Message);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnIncludeSystemChanged(bool value) =>
        ObservedTask.Run(LoadAppsAsync());

    partial void OnDebloatSearchTextChanged(string value) => ApplyDebloatFilter();

    partial void OnDebloatCategoryChanged(string value) => ApplyDebloatFilter();

    partial void OnShowDisabledOnlyChanged(bool value) => ObservedTask.Run(LoadDebloatAsync());

    private void ApplyFilter()
    {
        IEnumerable<AppItemViewModel> filtered = _allApps;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            filtered = _allApps.Where(a =>
                a.AppName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || a.PackageName.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        Apps = new ObservableCollection<AppItemViewModel>(filtered);
    }

    private void ApplyDebloatFilter()
    {
        IEnumerable<DebloatCandidate> filtered = _allDebloat;

        if (!string.IsNullOrWhiteSpace(DebloatCategory) && DebloatCategory != "Tümü")
            filtered = filtered.Where(c => c.Category == DebloatCategory);

        if (!string.IsNullOrWhiteSpace(DebloatSearchText))
        {
            filtered = filtered.Where(c =>
                c.AppName.Contains(DebloatSearchText, StringComparison.OrdinalIgnoreCase)
                || c.PackageName.Contains(DebloatSearchText, StringComparison.OrdinalIgnoreCase)
                || c.Category.Contains(DebloatSearchText, StringComparison.OrdinalIgnoreCase)
                || c.Reason.Contains(DebloatSearchText, StringComparison.OrdinalIgnoreCase));
        }

        DebloatCandidates = new ObservableCollection<DebloatCandidate>(
            filtered.OrderBy(c => c.Risk).ThenBy(c => c.AppName));
    }

    private async Task LoadIconsAsync(IEnumerable<AppItemViewModel> items)
    {
        foreach (var item in items)
            await LoadIconAsync(item);
    }
}
