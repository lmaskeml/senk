using System.Collections.ObjectModel;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Events;
using AndroidManager.Core.Models;
using AndroidManager.Core.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Prism.Navigation.Regions;
using Serilog;

namespace AndroidManager.Messages.ViewModels;

public sealed partial class ContactsViewModel : ObservableObject, IDisposable
{
    private readonly IContactsService _contacts;
    private readonly ISmsComposeBridge _composeBridge;
    private readonly IRegionManager _regionManager;
    private readonly IAdbService _adb;
    private readonly IAppDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private List<PhoneContact> _all = [];
    private bool _disposed;

    [ObservableProperty] private ObservableCollection<PhoneContact> _items = [];
    [ObservableProperty] private PhoneContact? _selectedContact;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private string _editPhone = string.Empty;
    [ObservableProperty] private string _editEmail = string.Empty;
    [ObservableProperty] private string _statusMessage = "Cihaz bekleniyor…";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isEditing;

    public ContactsViewModel(
        IContactsService contacts,
        ISmsComposeBridge composeBridge,
        IRegionManager regionManager,
        IAdbService adb,
        IAppDialogService dialogs,
        IUiDispatcher dispatcher)
    {
        _contacts = contacts;
        _composeBridge = composeBridge;
        _regionManager = regionManager;
        _adb = adb;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _logger = Log.ForContext<ContactsViewModel>();

        _adb.DeviceConnectionChanged += OnDeviceConnectionChanged;
        _adb.SelectedDeviceChanged += OnSelectedDeviceChanged;
    }

    public async Task InitializeAsync()
    {
        IsConnected = _adb.SelectedDevice is not null;
        if (IsConnected)
            await RefreshAsync();
        else
            StatusMessage = "Rehber için USB veya Wi‑Fi ile cihaz bağlayın";
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
            StatusMessage = "Kişiler yükleniyor…";
            _all = (await _contacts.GetContactsAsync()).ToList();
            ApplyFilter();
            StatusMessage = $"{_all.Count} kişi";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Contacts refresh failed");
            _all = [];
            Items.Clear();
            StatusMessage = "Rehber okunamadı";
            await _dialogs.ShowMessageAsync("Kişiler", ex.Message);
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
            var results = await _contacts.SearchAsync(SearchText.Trim());
            Items = new ObservableCollection<PhoneContact>(results);
            StatusMessage = $"{results.Count} sonuç";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("Kişi arama", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void NewContact()
    {
        SelectedContact = null;
        EditName = string.Empty;
        EditPhone = string.Empty;
        EditEmail = string.Empty;
        IsEditing = true;
    }

    [RelayCommand]
    private void BeginEdit()
    {
        if (SelectedContact is null)
            return;
        EditName = SelectedContact.DisplayName;
        EditPhone = SelectedContact.PhoneNumber;
        EditEmail = SelectedContact.Email;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        if (SelectedContact is not null)
            LoadEditFromSelection(SelectedContact);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            IsBusy = true;
            if (SelectedContact is null || SelectedContact.ContactId <= 0)
            {
                StatusMessage = "Kişi ekleniyor…";
                var created = await _contacts.CreateAsync(EditName, EditPhone, EditEmail);
                StatusMessage = $"Eklendi: {created.DisplayName}";
            }
            else
            {
                SelectedContact.DisplayName = EditName.Trim();
                SelectedContact.PhoneNumber = EditPhone.Trim();
                SelectedContact.Email = EditEmail.Trim();
                StatusMessage = "Kişi güncelleniyor…";
                await _contacts.UpdateAsync(SelectedContact);
                StatusMessage = "Kişi güncellendi";
            }

            IsEditing = false;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Contact save failed");
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("Kişiler", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedContact is null || SelectedContact.ContactId <= 0)
            return;

        var ok = await _dialogs.ShowConfirmationAsync(
            "Kişi sil",
            $"“{SelectedContact.DisplayName}” silinsin mi?");
        if (!ok)
            return;

        try
        {
            IsBusy = true;
            await _contacts.DeleteAsync(SelectedContact.ContactId);
            StatusMessage = "Kişi silindi";
            SelectedContact = null;
            IsEditing = false;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("Kişi sil", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SendSms()
    {
        if (SelectedContact is null || string.IsNullOrWhiteSpace(SelectedContact.PhoneNumber))
        {
            StatusMessage = "SMS için telefon numarası olan bir kişi seçin";
            return;
        }

        _composeBridge.RequestCompose(SelectedContact.PhoneNumber, SelectedContact.DisplayName);
        _regionManager.RequestNavigate(RegionNames.ContentRegion, ViewNames.Sms);
        StatusMessage = $"SMS: {SelectedContact.DisplayName}";
    }

    [RelayCommand]
    private async Task ExportVcfAsync() =>
        await ExportAsync("VCF", "contacts.vcf|*.vcf", _contacts.ExportToVcfAsync);

    [RelayCommand]
    private async Task ExportJsonAsync() =>
        await ExportAsync("JSON", null, _contacts.ExportToJsonAsync);

    [RelayCommand]
    private async Task ExportCsvAsync() =>
        await ExportAsync("CSV", null, _contacts.ExportToCsvAsync);

    [RelayCommand]
    private async Task ExportExcelAsync() =>
        await ExportAsync("Excel", null, _contacts.ExportToExcelAsync);

    [RelayCommand]
    private async Task ImportVcfAsync()
    {
        var files = await _dialogs.PickOpenFilesAsync(
            "VCF içe aktar",
            "vCard (*.vcf)|*.vcf|Tüm dosyalar (*.*)|*.*",
            multiSelect: false);
        if (files is null || files.Count == 0)
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "VCF içe aktarılıyor…";
            var count = await _contacts.ImportFromVcfAsync(files[0]);
            StatusMessage = $"{count} kişi içe aktarıldı";
            await _dialogs.ShowMessageAsync("VCF İçe Aktar", $"{count} kişi cihaza yazıldı.");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("VCF İçe Aktar", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportAsync(
        string label,
        string? saveFilter,
        Func<string, CancellationToken, Task<string>> export)
    {
        string? target;
        if (saveFilter is not null)
        {
            target = await _dialogs.PickSaveFileAsync(
                $"Kişiler {label} dışa aktar",
                saveFilter,
                $"contacts_{DateTime.Now:yyyyMMdd_HHmmss}.vcf");
        }
        else
        {
            target = await _dialogs.PickFolderAsync($"Kişiler {label} klasörü");
        }

        if (string.IsNullOrWhiteSpace(target))
            return;

        try
        {
            IsBusy = true;
            StatusMessage = $"{label} dışa aktarılıyor…";
            var path = await export(target, CancellationToken.None);
            StatusMessage = $"Kaydedildi: {path}";
            await _dialogs.ShowMessageAsync($"Kişiler {label}", $"Kaydedildi:\n{path}");
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync($"Kişiler {label}", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedContactChanged(PhoneContact? value)
    {
        if (value is null)
            return;
        LoadEditFromSelection(value);
        IsEditing = false;
    }

    private void LoadEditFromSelection(PhoneContact contact)
    {
        EditName = contact.DisplayName;
        EditPhone = contact.PhoneNumber;
        EditEmail = contact.Email;
    }

    private void ApplyFilter()
    {
        IEnumerable<PhoneContact> q = _all;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var s = SearchText.Trim();
            q = q.Where(c =>
                c.DisplayName.Contains(s, StringComparison.OrdinalIgnoreCase)
                || c.PhoneNumber.Contains(s, StringComparison.OrdinalIgnoreCase)
                || c.Email.Contains(s, StringComparison.OrdinalIgnoreCase));
        }

        Items = new ObservableCollection<PhoneContact>(q);
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
