using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Security.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AndroidManager.Security.ViewModels;

public sealed partial class RomBackupVaultViewModel : ObservableObject
{
    private readonly IRomBackupVaultService _vault;
    private readonly IRescueCenterService _rescue;
    private readonly IAppDialogService _dialogs;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;

    public ObservableCollection<RomPartitionItem> Partitions { get; } = [];
    public ObservableCollection<RomPartitionItem> RestorePartitions { get; } = [];
    public ObservableCollection<RomBackupSession> Sessions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBackup))]
    [NotifyPropertyChangedFor(nameof(CanStartBackup))]
    [NotifyPropertyChangedFor(nameof(MethodLabel))]
    private RomBackupCapability? _capability;
    [ObservableProperty] private RomBackupSession? _selectedSession;
    [ObservableProperty] private bool _backupKernel = true;
    [ObservableProperty] private bool _backupRadio = true;
    [ObservableProperty] private bool _backupSuper;
    [ObservableProperty] private bool _backupExtra;
    [ObservableProperty] private bool _restoreKernel;
    [ObservableProperty] private bool _restoreRadio;
    [ObservableProperty] private bool _restoreSuper;
    [ObservableProperty] private bool _restoreExtra;
    [ObservableProperty] private string _restorePartitionFilter = "";
    [ObservableProperty] private ICollectionView? _restorePartitionsView;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartBackup))]
    private bool _isBusy;
    [ObservableProperty] private int _progressPercent;
    [ObservableProperty] private string _statusMessage = "Cihaz TWRP veya root ile bağlanınca yedek alınabilir.";
    [ObservableProperty] private string _activityLog = "";

    public bool CanBackup => Capability?.CanBackup == true;
    public bool CanStartBackup => CanBackup && !IsBusy;
    public bool HasRestorePartitions => RestorePartitions.Count > 0;
    public int SelectedRestorePartitionCount => RestorePartitions.Count(p => p.IsSelected);
    public string SelectedRestoreSummary
    {
        get
        {
            var selected = RestorePartitions.Where(p => p.IsSelected).ToList();
            if (selected.Count == 0)
                return "Flash edilecek bölüm seçilmedi";
            var size = selected.Sum(p => p.SizeBytes);
            return $"{selected.Count} bölüm — {SizeFormatter.Format(size)}";
        }
    }
    public string MethodLabel => Capability?.Method switch
    {
        RomBackupMethod.TwrpDd => "TWRP ham dd",
        RomBackupMethod.LiveRoot => "Canlı root dd",
        _ => "Yedek alınamaz"
    };

    public RomBackupVaultViewModel(
        IRomBackupVaultService vault,
        IRescueCenterService rescue,
        IAppDialogService dialogs,
        ILogger? logger = null)
    {
        _vault = vault;
        _rescue = rescue;
        _dialogs = dialogs;
        _logger = logger ?? Log.ForContext<RomBackupVaultViewModel>();
    }

    public Task InitializeAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            Capability = await _vault.ProbeAsync();
            StatusMessage = Capability.Detail;

            Partitions.Clear();
            if (Capability.CanBackup)
            {
                var listed = await _vault.ListPartitionsAsync();
                foreach (var p in listed)
                {
                    Partitions.Add(new RomPartitionItem
                    {
                        Name = p.Name,
                        Category = p.Category,
                        SizeBytes = p.SizeBytes,
                        IsSlotAlias = p.IsSlotAlias,
                        SizeFormatted = p.SizeFormatted,
                        CategoryLabel = CategoryText(p.Category),
                        IsSelected = DefaultSelected(p)
                    });
                }
            }

            var keepFolder = SelectedSession?.Folder;
            Sessions.Clear();
            foreach (var session in await _vault.ListSessionsAsync())
                Sessions.Add(session);

            if (!string.IsNullOrWhiteSpace(keepFolder))
            {
                SelectedSession = Sessions.FirstOrDefault(s =>
                    string.Equals(s.Folder, keepFolder, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            _logger.Warning(ex, "[RomDump] Refresh failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StartBackupAsync()
    {
        var names = Partitions.Where(p => p.IsSelected && !p.IsSlotAlias).Select(p => p.Name).ToList();
        if (names.Count == 0)
        {
            await _dialogs.ShowMessageAsync("ROM yedeği", "En az bir bölüm seçin.");
            return;
        }

        if (BackupSuper)
        {
            var super = Partitions.FirstOrDefault(p =>
                p.IsSelected && p.Category == RomBackupCategory.SuperSystem);
            if (super is { SizeBytes: > 4L * 1024 * 1024 * 1024 }
                && !await _dialogs.ShowConfirmationAsync(
                    "Büyük super.img",
                    $"{super.Name} yaklaşık {super.SizeFormatted}. Döküm uzun sürebilir ve disk doldurabilir. Devam?"))
            {
                return;
            }
        }

        if (!await _dialogs.ShowConfirmationAsync(
                "Tam sistem yedeği",
                "Seçilen ham bölümler bilgisayara .img olarak yazılacak. USB'yi çıkarmayın."))
            return;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        ProgressPercent = 0;

        var progress = new Progress<RomBackupProgress>(p =>
        {
            ProgressPercent = p.Percent;
            StatusMessage = p.Message;
            AppendLog(p.Message);
        });

        try
        {
            var session = await _vault.CreateBackupAsync(
                new RomBackupRequest { PartitionNames = names },
                progress,
                _cts.Token);
            StatusMessage = $"Yedek alındı: {session.Folder}";
            AppendLog(session.Error is null ? "Tamam." : "Kısmi: " + session.Error);
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Yedekleme iptal edildi.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            _logger.Error(ex, "[RomDump] Backup failed");
            await _dialogs.ShowMessageAsync("ROM yedeği", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (SelectedSession is null)
        {
            await _dialogs.ShowMessageAsync("Geri yükle", "Bir yedek oturumu seçin.");
            return;
        }

        var names = RestorePartitions.Where(p => p.IsSelected).Select(p => p.Name).ToList();
        if (names.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Geri yükle", "En az bir bölüm seçin (ör. recovery).");
            return;
        }

        var selected = RestorePartitions.Where(p => p.IsSelected).ToList();
        var radio = selected.Any(p => p.Category == RomBackupCategory.RadioImei);
        if (radio && !await _dialogs.ShowConfirmationAsync(
                "IMEI / modem",
                "Seçilen bölümler modem/nvdata içeriyor. Yanlış cihaza yazmak IMEI'yi kalıcı bozabilir. Bu yedek bu telefona mı ait?"))
            return;

        var super = selected.FirstOrDefault(p => p.Category == RomBackupCategory.SuperSystem);
        if (super is not null
            && !await _dialogs.ShowConfirmationAsync(
                "super.img",
                $"{super.Name} flash edilecek. Bazı cihazlarda fastbootd gerekir. Devam?"))
            return;

        const long bootRecoveryWarnBytes = 100L * 1024 * 1024;
        var oversizedBootRecovery = selected
            .Where(p => FastbootPartitionHelper.IsBootOrRecoveryStem(p.Name) && p.SizeBytes > bootRecoveryWarnBytes)
            .ToList();
        if (oversizedBootRecovery.Count > 0)
        {
            var list = string.Join(", ", oversizedBootRecovery.Select(p =>
                $"{p.Name} ({p.SizeFormatted})"));
            if (!await _dialogs.ShowConfirmationAsync(
                    "Büyük boot/recovery yedeği",
                    $"{list}\n\nBu dosyalar genelde fastboot ile geri yazılamaz (Volume Full). " +
                    "Sadece recovery gerekiyorsa Recovery Manager → «Fastboot Flash Boot» ile .img kullanın.\n\nYine de denensin mi?"))
                return;
        }

        var partitionList = string.Join(", ", names.Take(8));
        if (names.Count > 8)
            partitionList += $" … (+{names.Count - 8})";

        if (!await _dialogs.ShowConfirmationAsync(
                "Fastboot restore",
                $"{SelectedSession.DisplayName}\n\nFlash edilecek: {partitionList}\n\nCihaz fastboot'ta olmalı. Devam?"))
            return;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        ProgressPercent = 0;

        var progress = new Progress<RomBackupProgress>(p =>
        {
            ProgressPercent = p.Percent;
            StatusMessage = p.Message;
            AppendLog(p.Message);
        });

        try
        {
            var result = await _vault.RestoreAsync(
                new RomBackupRestoreRequest
                {
                    Folder = SelectedSession.Folder,
                    PartitionNames = names
                },
                progress,
                _cts.Token);
            StatusMessage = result.Message;
            if (!result.Success)
                await _dialogs.ShowMessageAsync("Geri yükle", result.Message);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            await _dialogs.ShowMessageAsync("Geri yükle", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RebootFastbootAsync()
    {
        if (!await _dialogs.ShowConfirmationAsync("Fastboot", "Cihaz bootloader/fastboot moduna alınsın mı?"))
            return;
        var result = await _rescue.RebootFastbootAsync();
        StatusMessage = result.Message;
    }

    [RelayCommand]
    private void CancelWork() => _cts?.Cancel();

    partial void OnBackupKernelChanged(bool value) => ApplyCategory(Partitions, RomBackupCategory.KernelRecovery, value);
    partial void OnBackupRadioChanged(bool value) => ApplyCategory(Partitions, RomBackupCategory.RadioImei, value);
    partial void OnBackupSuperChanged(bool value) => ApplyCategory(Partitions, RomBackupCategory.SuperSystem, value);
    partial void OnBackupExtraChanged(bool value) => ApplyCategory(Partitions, RomBackupCategory.ExtraFirmware, value);

    partial void OnRestoreKernelChanged(bool value) => ApplyCategory(RestorePartitions, RomBackupCategory.KernelRecovery, value);
    partial void OnRestoreRadioChanged(bool value) => ApplyCategory(RestorePartitions, RomBackupCategory.RadioImei, value);
    partial void OnRestoreSuperChanged(bool value) => ApplyCategory(RestorePartitions, RomBackupCategory.SuperSystem, value);
    partial void OnRestoreExtraChanged(bool value) => ApplyCategory(RestorePartitions, RomBackupCategory.ExtraFirmware, value);

    partial void OnSelectedSessionChanged(RomBackupSession? value) => LoadRestorePartitions(value);

    partial void OnRestorePartitionFilterChanged(string value) => RestorePartitionsView?.Refresh();

    [RelayCommand]
    private void SelectAllRestorePartitions()
    {
        foreach (var item in RestorePartitions)
            item.IsSelected = true;
    }

    [RelayCommand]
    private void ClearRestorePartitionSelection()
    {
        foreach (var item in RestorePartitions)
            item.IsSelected = false;
    }

    [RelayCommand]
    private void SelectRecoveryOnly()
    {
        foreach (var item in RestorePartitions)
        {
            item.IsSelected = item.Name.Contains("recovery", StringComparison.OrdinalIgnoreCase);
        }
    }

    [RelayCommand]
    private void SelectModemOnly()
    {
        foreach (var item in RestorePartitions)
        {
            var name = item.Name;
            item.IsSelected = name.Contains("modem", StringComparison.OrdinalIgnoreCase)
                              || name.Contains("nvdata", StringComparison.OrdinalIgnoreCase)
                              || name.Equals("persist", StringComparison.OrdinalIgnoreCase)
                              || name.StartsWith("fsg", StringComparison.OrdinalIgnoreCase)
                              || name.StartsWith("mdm", StringComparison.OrdinalIgnoreCase);
        }
    }

    [RelayCommand]
    private void SelectRestoreKernelOnly()
    {
        foreach (var item in RestorePartitions)
            item.IsSelected = item.Category == RomBackupCategory.KernelRecovery;
    }

    private void LoadRestorePartitions(RomBackupSession? session)
    {
        foreach (var item in RestorePartitions)
            item.PropertyChanged -= OnRestorePartitionItemChanged;
        RestorePartitions.Clear();
        RestorePartitionsView = null;

        if (session is null)
        {
            NotifyRestoreSelection();
            return;
        }

        foreach (var part in session.Partitions)
        {
            var item = new RomPartitionItem
            {
                Name = part.Name,
                Category = part.Category,
                SizeBytes = part.SizeBytes,
                SizeFormatted = FormatPartitionSize(part.SizeBytes),
                CategoryLabel = CategoryText(part.Category),
                IsSelected = DefaultRestoreSelected(part)
            };
            item.PropertyChanged += OnRestorePartitionItemChanged;
            RestorePartitions.Add(item);
        }

        RestorePartitionsView = CollectionViewSource.GetDefaultView(RestorePartitions);
        if (RestorePartitionsView is not null)
            RestorePartitionsView.Filter = FilterRestorePartition;
        RestorePartitionsView?.Refresh();
        NotifyRestoreSelection();
    }

    private void OnRestorePartitionItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RomPartitionItem.IsSelected))
            NotifyRestoreSelection();
    }

    private bool FilterRestorePartition(object obj)
    {
        if (obj is not RomPartitionItem item)
            return false;
        if (string.IsNullOrWhiteSpace(RestorePartitionFilter))
            return true;
        return item.Name.Contains(RestorePartitionFilter.Trim(), StringComparison.OrdinalIgnoreCase)
               || item.CategoryLabel.Contains(RestorePartitionFilter.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private void NotifyRestoreSelection()
    {
        OnPropertyChanged(nameof(HasRestorePartitions));
        OnPropertyChanged(nameof(SelectedRestorePartitionCount));
        OnPropertyChanged(nameof(SelectedRestoreSummary));
    }

    private bool DefaultRestoreSelected(RomBackupPartitionFile part) => part.Category switch
    {
        RomBackupCategory.KernelRecovery => RestoreKernel,
        RomBackupCategory.RadioImei => RestoreRadio,
        RomBackupCategory.SuperSystem => RestoreSuper,
        RomBackupCategory.ExtraFirmware => RestoreExtra,
        _ => false
    };

    private void ApplyCategory(
        ObservableCollection<RomPartitionItem> items,
        RomBackupCategory category,
        bool selected)
    {
        foreach (var item in items.Where(p => p.Category == category && !p.IsSlotAlias))
            item.IsSelected = selected;
    }

    private bool DefaultSelected(DevicePartitionInfo p)
    {
        if (p.IsSlotAlias)
            return false;
        return p.Category switch
        {
            RomBackupCategory.KernelRecovery => BackupKernel,
            RomBackupCategory.RadioImei => BackupRadio,
            RomBackupCategory.SuperSystem => BackupSuper,
            RomBackupCategory.ExtraFirmware => BackupExtra,
            _ => false
        };
    }

    private static string CategoryText(RomBackupCategory category) => category switch
    {
        RomBackupCategory.KernelRecovery => "Çekirdek",
        RomBackupCategory.RadioImei => "Şebeke / IMEI",
        RomBackupCategory.SuperSystem => "Sistem (super)",
        RomBackupCategory.ExtraFirmware => "Firmware",
        _ => category.ToString()
    };

    private static string FormatPartitionSize(long bytes) => bytes switch
    {
        <= 0 => "?",
        _ => SizeFormatter.Format(bytes)
    };

    private void AppendLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        var next = $"[{DateTime.Now:HH:mm:ss}] {line.Trim()}";
        ActivityLog = string.IsNullOrEmpty(ActivityLog) ? next : ActivityLog + Environment.NewLine + next;
        if (ActivityLog.Length > 12_000)
            ActivityLog = ActivityLog[^8_000..];
    }
}
