using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using AndroidManager.Core;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Events;
using AndroidManager.Core.Models;
using AndroidManager.Gallery.Services;
using AndroidManager.Gallery.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AndroidManager.Gallery.ViewModels;

public sealed partial class GalleryViewModel : ObservableObject, IDisposable
{
    private readonly IGalleryService _gallery;
    private readonly IAdbService _adb;
    private readonly IAppDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly IThumbnailCache _thumbCache;
    private readonly ILogger _logger;
    private CancellationTokenSource? _loadCts;
    private int _refreshGeneration;
    private string? _boundSerial;
    private bool _suppressFilterRefresh;
    private bool _disposed;

    [ObservableProperty] private ObservableCollection<GalleryItemViewModel> _items = [];
    [ObservableProperty] private ObservableCollection<GalleryTimelineGroup> _timelineGroups = [];
    [ObservableProperty] private GalleryItemViewModel? _selectedItem;
    [ObservableProperty] private string _statusMessage = "Cihaz bekleniyor…";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _filterMode = "Hepsi";
    [ObservableProperty] private string _viewMode = "Izgara";
    [ObservableProperty] private int _loadedCount;

    public IReadOnlyList<string> FilterModes { get; } = ["Hepsi", "Fotoğraflar", "Videolar"];
    public IReadOnlyList<string> ViewModes { get; } = ["Izgara", "Zaman çizelgesi"];
    public bool IsTimelineMode => ViewMode == "Zaman çizelgesi";
    public bool IsGridMode => ViewMode != "Zaman çizelgesi";

    public GalleryViewModel(
        IGalleryService gallery,
        IAdbService adb,
        IAppDialogService dialogs,
        IUiDispatcher dispatcher,
        IThumbnailCache thumbCache)
    {
        _gallery = gallery;
        _adb = adb;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _thumbCache = thumbCache;
        _logger = Log.ForContext<GalleryViewModel>();

        _adb.DeviceConnectionChanged += OnDeviceConnectionChanged;
        _adb.SelectedDeviceChanged += OnSelectedDeviceChanged;
    }

    public async Task InitializeAsync()
    {
        IsConnected = _adb.SelectedDevice is not null;
        if (IsConnected)
        {
            _suppressFilterRefresh = true;
            try
            {
                await RefreshAsync();
            }
            finally
            {
                _suppressFilterRefresh = false;
            }
        }
        else
            StatusMessage = "Galeri için USB veya Wi‑Fi ile cihaz bağlayın";
    }

    partial void OnFilterModeChanged(string value)
    {
        if (_suppressFilterRefresh || _disposed)
            return;
        ObservedTask.Run(RefreshAsync());
    }

    partial void OnViewModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsTimelineMode));
        OnPropertyChanged(nameof(IsGridMode));
        RebuildTimeline();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_adb.SelectedDevice is null)
        {
            StatusMessage = "Cihaz bağlı değil";
            IsConnected = false;
            _boundSerial = null;
            return;
        }

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;
        var generation = Interlocked.Increment(ref _refreshGeneration);

        IsBusy = true;
        StatusMessage = "MediaStore taranıyor…";
        try
        {
            GalleryMediaKind? kind = FilterMode switch
            {
                "Fotoğraflar" => GalleryMediaKind.Image,
                "Videolar" => GalleryMediaKind.Video,
                _ => null
            };

            var media = await _gallery.GetMediaAsync(kind, limit: 800, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();

            await _dispatcher.InvokeAsync(() =>
            {
                if (generation != _refreshGeneration)
                    return;

                Items.Clear();
                foreach (var item in media)
                    Items.Add(new GalleryItemViewModel(item, _gallery, _dispatcher, _thumbCache));
                LoadedCount = Items.Count;
                RebuildTimeline();
                _boundSerial = _adb.SelectedDevice?.Serial;
                StatusMessage = LoadedCount == 0
                    ? "Medya bulunamadı (izin veya boş depolama)"
                    : $"{LoadedCount} öğe · {(IsTimelineMode ? "zaman çizelgesi" : "ızgara")}";
            });
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer refresh
        }
        catch (Exception ex)
        {
            if (generation != _refreshGeneration)
                return;

            _logger.Error(ex, "Gallery refresh failed");
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("Galeri", ex.Message);
        }
        finally
        {
            if (generation == _refreshGeneration)
                IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OpenPreviewAsync(GalleryItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null)
            return;

        // Open window immediately — do not freeze gallery behind IsBusy / thumb queue.
        StatusMessage = $"Önizleme: {item.Title}";
        var owner = Application.Current?.MainWindow;
        var win = new GalleryPreviewWindow(item.Item) { Owner = owner };
        win.Show();
        try
        {
            await win.LoadAsync(_gallery);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Preview failed");
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task SaveToPcAsync(GalleryItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null)
            return;

        var dest = await _dialogs.PickSaveFileAsync(
            "Galeri öğesini kaydet",
            "Tüm dosyalar|*.*",
            item.Title);
        if (string.IsNullOrWhiteSpace(dest))
            return;

        IsBusy = true;
        try
        {
            var path = await _gallery.PullOriginalAsync(item.Item);
            await Task.Run(() => File.Copy(path, dest, overwrite: true));
            StatusMessage = $"Kaydedildi: {dest}";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("Kaydet", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnDeviceConnectionChanged(object? sender, DeviceConnectionChangedEventArgs e)
    {
        _dispatcher.Observe(async () =>
        {
            if (_disposed) return;

            IsConnected = _adb.SelectedDevice is not null;
            if (!IsConnected)
            {
                Items.Clear();
                TimelineGroups.Clear();
                LoadedCount = 0;
                _boundSerial = null;
                StatusMessage = "Cihaz bağlı değil";
                return;
            }

            var serial = _adb.SelectedDevice!.Serial;
            // Same device still online — ignore watchdog/noise reconnects.
            if (string.Equals(serial, _boundSerial, StringComparison.OrdinalIgnoreCase)
                && Items.Count > 0)
                return;

            await RefreshAsync();
        });
    }

    private void OnSelectedDeviceChanged(object? sender, ConnectedDevice? device)
    {
        _dispatcher.Observe(async () =>
        {
            if (_disposed) return;

            IsConnected = device is not null;
            if (device is null)
            {
                Items.Clear();
                TimelineGroups.Clear();
                LoadedCount = 0;
                _boundSerial = null;
                _thumbCache.Clear();
                StatusMessage = "Cihaz bağlı değil";
                return;
            }

            if (!string.Equals(device.Serial, _boundSerial, StringComparison.OrdinalIgnoreCase))
                _thumbCache.Clear();

            if (string.Equals(device.Serial, _boundSerial, StringComparison.OrdinalIgnoreCase)
                && Items.Count > 0)
                return;

            await RefreshAsync();
        });
    }

    private void RebuildTimeline()
    {
        TimelineGroups.Clear();
        if (Items.Count == 0)
            return;

        var today = DateTime.Today;
        var groups = Items
            .GroupBy(i =>
            {
                var d = i.Item.DateTakenLocal.Date;
                if (d == default) return (Key: "Tarihsiz", Order: int.MaxValue, Date: DateTime.MaxValue);
                if (d == today) return (Key: "Bugün", Order: 0, Date: d);
                if (d == today.AddDays(-1)) return (Key: "Dün", Order: 1, Date: d);
                if (d >= today.AddDays(-7)) return (Key: "Bu hafta", Order: 2, Date: d);
                return (Key: d.ToString("d MMMM yyyy"), Order: 3, Date: d);
            })
            .OrderBy(g => g.Key.Order)
            .ThenByDescending(g => g.Key.Date)
            .Select(g => new GalleryTimelineGroup(g.Key.Key, g));

        foreach (var group in groups)
            TimelineGroups.Add(group);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _adb.DeviceConnectionChanged -= OnDeviceConnectionChanged;
        _adb.SelectedDeviceChanged -= OnSelectedDeviceChanged;
        Interlocked.Increment(ref _refreshGeneration);
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }
}
