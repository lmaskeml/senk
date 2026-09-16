using System.Collections.ObjectModel;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Events;
using AndroidManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AndroidManager.Messages.ViewModels;

public sealed partial class CallLogViewModel : ObservableObject, IDisposable
{
    private readonly ICallLogService _calls;
    private readonly IAdbService _adb;
    private readonly IAppDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private List<CallLogEntry> _all = [];
    private bool _disposed;

    [ObservableProperty] private ObservableCollection<CallLogEntry> _items = [];
    [ObservableProperty] private CallLogEntry? _selectedCall;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _statusMessage = "Cihaz bekleniyor…";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isConnected;

    public CallLogViewModel(
        ICallLogService calls,
        IAdbService adb,
        IAppDialogService dialogs,
        IUiDispatcher dispatcher)
    {
        _calls = calls;
        _adb = adb;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _logger = Log.ForContext<CallLogViewModel>();

        _adb.DeviceConnectionChanged += OnDeviceConnectionChanged;
        _adb.SelectedDeviceChanged += OnSelectedDeviceChanged;
    }

    public async Task InitializeAsync()
    {
        IsConnected = _adb.SelectedDevice is not null;
        if (IsConnected)
            await RefreshAsync();
        else
            StatusMessage = "Arama geçmişi için USB veya Wi‑Fi ile cihaz bağlayın";
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_adb.SelectedDevice is null)
        {
            StatusMessage = "Cihaz bağlı değil";
            IsConnected = false;
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Arama geçmişi yükleniyor…";
            _all = (await _calls.GetCallsAsync()).ToList();
            ApplyFilter();
            StatusMessage = $"{_all.Count} kayıt";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Call log refresh failed");
            _all = [];
            Items.Clear();
            StatusMessage = "Arama geçmişi okunamadı";
            await _dialogs.ShowMessageAsync("Arama geçmişi", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            ApplyFilter();
            return;
        }

        try
        {
            IsBusy = true;
            var results = await _calls.SearchAsync(SearchText.Trim());
            Items = new ObservableCollection<CallLogEntry>(results);
            StatusMessage = $"{results.Count} sonuç";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("Arama", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportJsonAsync() =>
        await ExportAsync("JSON", _calls.ExportToJsonAsync);

    [RelayCommand]
    private async Task ExportCsvAsync() =>
        await ExportAsync("CSV", _calls.ExportToCsvAsync);

    [RelayCommand]
    private async Task ExportExcelAsync() =>
        await ExportAsync("Excel", _calls.ExportToExcelAsync);

    private async Task ExportAsync(string label, Func<string, CancellationToken, Task<string>> export)
    {
        var folder = await _dialogs.PickFolderAsync($"Arama geçmişi {label} klasörü");
        if (string.IsNullOrWhiteSpace(folder))
            return;

        try
        {
            IsBusy = true;
            StatusMessage = $"{label} dışa aktarılıyor…";
            var path = await export(folder, CancellationToken.None);
            StatusMessage = $"Kaydedildi: {path}";
            await _dialogs.ShowMessageAsync($"Arama {label}", $"Kaydedildi:\n{path}");
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync($"Arama {label}", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        IEnumerable<CallLogEntry> q = _all;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var s = SearchText.Trim();
            q = q.Where(c =>
                c.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
                || c.Number.Contains(s, StringComparison.OrdinalIgnoreCase)
                || c.TypeLabel.Contains(s, StringComparison.OrdinalIgnoreCase));
        }

        Items = new ObservableCollection<CallLogEntry>(q);
    }

    private void OnDeviceConnectionChanged(object? sender, DeviceConnectionChangedEventArgs e) =>
        _dispatcher.Observe(async () =>
        {
            IsConnected = _adb.SelectedDevice is not null;
            if (IsConnected)
                await RefreshAsync();
            else
            {
                StatusMessage = "Cihaz bağlantısı kesildi";
                Items.Clear();
                _all = [];
            }
        });

    private void OnSelectedDeviceChanged(object? sender, ConnectedDevice? device) =>
        _dispatcher.Observe(async () =>
        {
            IsConnected = device is not null;
            if (device is not null)
                await RefreshAsync();
            else
            {
                StatusMessage = "Cihaz seçilmedi";
                Items.Clear();
                _all = [];
            }
        });

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _adb.DeviceConnectionChanged -= OnDeviceConnectionChanged;
        _adb.SelectedDeviceChanged -= OnSelectedDeviceChanged;
    }
}
