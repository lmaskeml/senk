using System.Collections.ObjectModel;
using AndroidManager.Core;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Events;
using AndroidManager.Core.Models;
using AndroidManager.Core.Parsing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AndroidManager.Messages.ViewModels;

public sealed partial class SmsViewModel : ObservableObject, IDisposable
{
    private readonly ISmsService _sms;
    private readonly IContactsService _contacts;
    private readonly ISmsComposeBridge _composeBridge;
    private readonly IAdbService _adb;
    private readonly IAppDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private List<SmsThread> _allThreads = [];
    private List<PhoneContact> _allContacts = [];
    private bool _disposed;

    [ObservableProperty] private ObservableCollection<SmsThread> _threads = [];
    [ObservableProperty] private ObservableCollection<PhoneContact> _contactItems = [];
    [ObservableProperty] private SmsThread? _selectedThread;
    [ObservableProperty] private PhoneContact? _selectedContact;
    [ObservableProperty] private ObservableCollection<SmsMessage> _messages = [];
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _composeAddress = string.Empty;
    [ObservableProperty] private string _composeBody = string.Empty;
    [ObservableProperty] private string _composeRecipientHint = string.Empty;
    [ObservableProperty] private string _statusMessage = "Cihaz bekleniyor…";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private int _leftPanelTabIndex;

    public SmsViewModel(
        ISmsService sms,
        IContactsService contacts,
        ISmsComposeBridge composeBridge,
        IAdbService adb,
        IAppDialogService dialogs,
        IUiDispatcher dispatcher)
    {
        _sms = sms;
        _contacts = contacts;
        _composeBridge = composeBridge;
        _adb = adb;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _logger = Log.ForContext<SmsViewModel>();

        _adb.DeviceConnectionChanged += OnDeviceConnectionChanged;
        _adb.SelectedDeviceChanged += OnSelectedDeviceChanged;
        _composeBridge.ComposeRequested += OnComposeRequested;
    }

    public async Task InitializeAsync()
    {
        IsConnected = _adb.SelectedDevice is not null;
        if (IsConnected)
            await RefreshAsync();
        else
            StatusMessage = "SMS için USB veya Wi‑Fi ile cihaz bağlayın";

        ApplyPendingCompose();
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
            StatusMessage = "SMS ve rehber yükleniyor…";

            var threadTask = _sms.GetThreadsAsync();
            var contactTask = LoadContactsSafeAsync();
            await Task.WhenAll(threadTask, contactTask).ConfigureAwait(true);

            // WhenAll sonrası .Result deadlock riski taşır; sonucu await ile al.
            _allThreads = (await threadTask.ConfigureAwait(true)).ToList();
            ApplyFilter();

            var threadCount = _allThreads.Count;
            var contactCount = _allContacts.Count;
            StatusMessage = contactCount > 0
                ? $"{threadCount} konuşma • {contactCount} rehber kişisi"
                : threadCount == 0
                    ? "0 konuşma — kutuda SMS yok veya cihaz boş döndü"
                    : $"{threadCount} konuşma";

            if (SelectedThread is not null)
            {
                var refreshed = _allThreads.FirstOrDefault(t =>
                    PhoneNumberNormalizer.TryMatch(t.Address, SelectedThread.Address));
                if (refreshed is not null)
                    await LoadThreadAsync(refreshed);
            }

            ApplyPendingCompose();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "SMS refresh failed");
            Threads.Clear();
            Messages.Clear();
            _allThreads = [];
            StatusMessage = "SMS okunamadı";
            await _dialogs.ShowMessageAsync("SMS okunamadı", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadContactsSafeAsync()
    {
        try
        {
            _allContacts = (await _contacts.GetContactsAsync().ConfigureAwait(true))
                .Where(c => !string.IsNullOrWhiteSpace(c.PhoneNumber))
                .ToList();
            ApplyContactFilter();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Rehber SMS sekmesine yüklenemedi");
            _allContacts = [];
            ContactItems.Clear();
            if (_allThreads.Count == 0)
                StatusMessage = "Rehber okunamadı — yalnızca numaralar gösterilecek";
        }
    }

    [RelayCommand]
    private async Task LoadThreadAsync(SmsThread? thread)
    {
        if (thread is null) return;
        SelectedThread = thread;
        SelectedContact = null;
        ComposeAddress = thread.Address;
        UpdateComposeRecipientHint(thread.Address);

        try
        {
            IsBusy = true;
            var items = await _sms.GetThreadMessagesAsync(thread.Address);
            Messages = new ObservableCollection<SmsMessage>(items);
            StatusMessage = $"{thread.EffectiveDisplayName} — {items.Count} mesaj";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void StartFromContact(PhoneContact? contact)
    {
        if (contact is null || string.IsNullOrWhiteSpace(contact.PhoneNumber))
        {
            StatusMessage = "Bu kişinin telefon numarası yok";
            return;
        }

        SelectedThread = null;
        SelectedContact = contact;
        Messages.Clear();
        ComposeAddress = contact.PhoneNumber;
        ComposeBody = string.Empty;
        UpdateComposeRecipientHint(contact.PhoneNumber, contact.DisplayName);
        StatusMessage = $"Yeni mesaj: {contact.DisplayName}";
    }

    [RelayCommand]
    private void NewMessage()
    {
        SelectedThread = null;
        SelectedContact = null;
        Messages.Clear();
        ComposeAddress = string.Empty;
        ComposeBody = string.Empty;
        ComposeRecipientHint = string.Empty;
        StatusMessage = "Yeni SMS — rehberden kişi seçin veya numara yazın";
        LeftPanelTabIndex = _allContacts.Count > 0 ? 1 : 0;
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(ComposeAddress) || string.IsNullOrWhiteSpace(ComposeBody))
        {
            StatusMessage = "Numara ve mesaj gerekli";
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Gönderiliyor…";
            var result = await _sms.SendAsync(ComposeAddress, ComposeBody);
            StatusMessage = result.Message;

            if (result.RequiresUserConfirm)
                await _dialogs.ShowMessageAsync("SMS", result.Message);

            if (result.Success)
            {
                ComposeBody = string.Empty;
                await RefreshAsync();
                var thread = _allThreads.FirstOrDefault(t =>
                    PhoneNumberNormalizer.TryMatch(t.Address, ComposeAddress));
                if (thread is not null)
                    await LoadThreadAsync(thread);
            }
            else
            {
                await _dialogs.ShowMessageAsync("SMS", result.Message);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("SMS", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        var folder = await _dialogs.PickFolderAsync("SMS JSON dışa aktarma klasörü");
        if (string.IsNullOrWhiteSpace(folder))
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "JSON dışa aktarılıyor…";
            var path = await _sms.ExportToJsonAsync(folder);
            StatusMessage = $"JSON kaydedildi: {path}";
            await _dialogs.ShowMessageAsync("SMS Export", $"Kaydedildi:\n{path}");
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("SMS Export", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var folder = await _dialogs.PickFolderAsync("SMS CSV dışa aktarma klasörü");
        if (string.IsNullOrWhiteSpace(folder))
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "CSV dışa aktarılıyor…";
            var path = await _sms.ExportToCsvAsync(folder);
            StatusMessage = $"CSV kaydedildi: {path}";
            await _dialogs.ShowMessageAsync("SMS Export", $"Kaydedildi:\n{path}");
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("SMS Export", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SearchMessagesAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            ApplyFilter();
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Mesajlarda aranıyor…";
            var results = await _sms.SearchAsync(SearchText.Trim());
            Messages = new ObservableCollection<SmsMessage>(results);
            SelectedThread = null;
            SelectedContact = null;
            LeftPanelTabIndex = 0;
            StatusMessage = $"{results.Count} mesaj bulundu";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("SMS Arama", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
        ApplyContactFilter();
    }

    partial void OnComposeAddressChanged(string value) => UpdateComposeRecipientHint(value);

    partial void OnSelectedThreadChanged(SmsThread? value)
    {
        if (value is not null)
            ObservedTask.Run(LoadThreadAsync(value));
    }

    private void ApplyFilter()
    {
        IEnumerable<SmsThread> filtered = _allThreads;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText.Trim();
            filtered = _allThreads.Where(t =>
                t.Address.Contains(q, StringComparison.OrdinalIgnoreCase)
                || t.EffectiveDisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || t.LastBody.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        Threads = new ObservableCollection<SmsThread>(filtered);
    }

    private void ApplyContactFilter()
    {
        IEnumerable<PhoneContact> filtered = _allContacts;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText.Trim();
            filtered = _allContacts.Where(c =>
                c.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.PhoneNumber.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Email.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        ContactItems = new ObservableCollection<PhoneContact>(
            filtered.Where(c => !string.IsNullOrWhiteSpace(c.PhoneNumber)));
    }

    private void UpdateComposeRecipientHint(string address, string? knownName = null)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            ComposeRecipientHint = string.Empty;
            return;
        }

        var name = knownName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = _allContacts.FirstOrDefault(c =>
                PhoneNumberNormalizer.TryMatch(c.PhoneNumber, address))?.DisplayName;
        }

        ComposeRecipientHint = string.IsNullOrWhiteSpace(name) || PhoneNumberNormalizer.TryMatch(name, address)
            ? string.Empty
            : $"Alıcı: {name}";
    }

    private void ApplyPendingCompose()
    {
        if (string.IsNullOrWhiteSpace(_composeBridge.PendingAddress))
            return;

        var address = _composeBridge.PendingAddress;
        var name = _composeBridge.PendingDisplayName;
        _composeBridge.ClearPending();

        SelectedThread = null;
        Messages.Clear();
        ComposeAddress = address;
        ComposeBody = string.Empty;
        UpdateComposeRecipientHint(address, name);
        LeftPanelTabIndex = 1;

        var contact = _allContacts.FirstOrDefault(c =>
            PhoneNumberNormalizer.TryMatch(c.PhoneNumber, address));
        SelectedContact = contact;
        StatusMessage = string.IsNullOrWhiteSpace(name)
            ? $"Yeni mesaj: {address}"
            : $"Yeni mesaj: {name}";
    }

    private void OnComposeRequested() =>
        _dispatcher.Observe(ApplyPendingCompose);

    private void OnDeviceConnectionChanged(object? sender, DeviceConnectionChangedEventArgs e)
    {
        _dispatcher.Observe(async () =>
        {
            IsConnected = _adb.SelectedDevice is not null;
            if (IsConnected)
                await RefreshAsync();
            else
            {
                Threads.Clear();
                ContactItems.Clear();
                Messages.Clear();
                _allThreads = [];
                _allContacts = [];
                StatusMessage = "Cihaz bağlı değil";
            }
        });
    }

    private void OnSelectedDeviceChanged(object? sender, ConnectedDevice? device)
    {
        _dispatcher.Observe(async () =>
        {
            IsConnected = device is not null;
            if (IsConnected)
                await RefreshAsync();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _adb.DeviceConnectionChanged -= OnDeviceConnectionChanged;
        _adb.SelectedDeviceChanged -= OnSelectedDeviceChanged;
        _composeBridge.ComposeRequested -= OnComposeRequested;
    }
}
