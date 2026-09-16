using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AndroidManager.Files.ViewModels;

public sealed partial class FileManagerViewModel : ObservableObject
{
    private const string PcDrivesRoot = "Bilgisayar";

    private readonly IFileService _fileService;
    private readonly IAppService _appService;
    private readonly IAppDialogService _dialogs;

    [ObservableProperty] private ObservableCollection<FileItemViewModel> _androidItems = [];
    [ObservableProperty] private FileItemViewModel? _selectedAndroidItem;
    [ObservableProperty] private string _androidCurrentPath = "/sdcard";
    [ObservableProperty] private ObservableCollection<BreadcrumbItem> _androidBreadcrumb = [];

    [ObservableProperty] private ObservableCollection<PcFileItemViewModel> _pcItems = [];
    [ObservableProperty] private PcFileItemViewModel? _selectedPcItem;
    [ObservableProperty] private string _pcCurrentPath =
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

    [ObservableProperty] private bool _isTransferring;
    [ObservableProperty] private TransferProgress _currentProgress = new();
    [ObservableProperty] private string _statusMessage = "Hazır";
    [ObservableProperty] private bool _isAndroidLoading;
    [ObservableProperty] private bool _isPcLoading;

    public FileManagerViewModel(IFileService fileService, IAppService appService, IAppDialogService dialogs)
    {
        _fileService = fileService;
        _appService = appService;
        _dialogs = dialogs;
    }

    public async Task InitializeAsync()
    {
        await NavigateAndroidAsync("/sdcard");
        await NavigatePcAsync(PcCurrentPath);
    }

    [RelayCommand]
    private async Task NavigateAndroidAsync(string path)
    {
        IsAndroidLoading = true;
        StatusMessage = $"Yükleniyor: {path}";
        try
        {
            var items = await _fileService.ListDirectoryAsync(path);
            AndroidCurrentPath = path;
            AndroidItems = new ObservableCollection<FileItemViewModel>(
                items.Select(i => new FileItemViewModel(i)));
            BuildAndroidBreadcrumb(path);
            StatusMessage = $"{items.Count} öğe";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Android dizin yükleme hatası: {Path}", path);
            StatusMessage = $"Hata: {ex.Message}";
        }
        finally
        {
            IsAndroidLoading = false;
        }
    }

    [RelayCommand]
    private async Task OpenAndroidItemAsync(FileItemViewModel? item)
    {
        if (item?.IsDirectory == true)
            await NavigateAndroidAsync(item.FullPath);
    }

    [RelayCommand]
    private async Task NavigateAndroidUpAsync()
    {
        if (AndroidCurrentPath is "/" or "")
            return;

        var slash = AndroidCurrentPath.TrimEnd('/').LastIndexOf('/');
        var parent = slash <= 0 ? "/" : AndroidCurrentPath[..slash];
        await NavigateAndroidAsync(parent);
    }

    [RelayCommand]
    private Task NavigatePcAsync(string path)
    {
        if (IsPcDrivesRoot(path))
            return LoadPcDrivesAsync();

        IsPcLoading = true;
        try
        {
            var dir = new DirectoryInfo(path);
            if (!dir.Exists)
                throw new DirectoryNotFoundException(path);

            var items = new List<PcFileItemViewModel>();
            foreach (var d in dir.GetDirectories().OrderBy(x => x.Name))
                items.Add(new PcFileItemViewModel(d.FullName, true, 0, d.LastWriteTime));
            foreach (var f in dir.GetFiles().OrderBy(x => x.Name))
                items.Add(new PcFileItemViewModel(f.FullName, false, f.Length, f.LastWriteTime));

            PcCurrentPath = path;
            PcItems = new ObservableCollection<PcFileItemViewModel>(items);
            StatusMessage = $"PC: {items.Count} öğe";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PC dizin yükleme hatası: {Path}", path);
            StatusMessage = $"Hata: {ex.Message}";
        }
        finally
        {
            IsPcLoading = false;
        }

        return Task.CompletedTask;
    }

    private Task LoadPcDrivesAsync()
    {
        IsPcLoading = true;
        try
        {
            var items = new List<PcFileItemViewModel>();
            foreach (var drive in DriveInfo.GetDrives().OrderBy(d => d.Name))
            {
                if (!drive.IsReady)
                    continue;

                var letter = drive.Name.TrimEnd('\\');
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? drive.DriveType switch
                    {
                        DriveType.Removable => "Çıkarılabilir Disk",
                        DriveType.Network => "Ağ Sürücüsü",
                        DriveType.CDRom => "CD Sürücüsü",
                        DriveType.Ram => "RAM Disk",
                        _ => "Yerel Disk"
                    }
                    : drive.VolumeLabel;
                items.Add(new PcFileItemViewModel(
                    drive.Name,
                    isDirectory: true,
                    size: 0,
                    modified: default,
                    name: $"{label} ({letter})"));
            }

            PcCurrentPath = PcDrivesRoot;
            PcItems = new ObservableCollection<PcFileItemViewModel>(items);
            StatusMessage = $"PC: {items.Count} sürücü";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PC sürücü listesi yükleme hatası");
            StatusMessage = $"Hata: {ex.Message}";
        }
        finally
        {
            IsPcLoading = false;
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task OpenPcItemAsync(PcFileItemViewModel? item)
    {
        if (item?.IsDirectory == true)
            await NavigatePcAsync(item.FullPath);
    }

    [RelayCommand]
    private async Task NavigatePcUpAsync()
    {
        if (IsPcDrivesRoot(PcCurrentPath))
            return;

        var parent = Directory.GetParent(PcCurrentPath);
        if (parent is not null)
            await NavigatePcAsync(parent.FullName);
        else
            await LoadPcDrivesAsync();
    }

    [RelayCommand(CanExecute = nameof(CanTransfer))]
    private async Task PullAsync()
    {
        if (SelectedAndroidItem is null)
            return;

        if (IsPcDrivesRoot(PcCurrentPath))
        {
            StatusMessage = "İndirmek için önce bir sürücü veya klasör açın.";
            return;
        }

        var dest = Path.Combine(PcCurrentPath, SelectedAndroidItem.Name);
        var label = SelectedAndroidItem.IsDirectory ? "Klasör indiriliyor" : "İndiriliyor";
        await RunTransferAsync(
            () => _fileService.PullAsync(
                SelectedAndroidItem.FullPath,
                dest,
                new Progress<TransferProgress>(p => CurrentProgress = p)),
            $"{label}: {SelectedAndroidItem.Name}",
            SelectedAndroidItem.IsDirectory ? "Klasör indirme tamamlandı." : "İndirme tamamlandı.");

        await NavigatePcAsync(PcCurrentPath);
    }

    [RelayCommand(CanExecute = nameof(CanTransfer))]
    private async Task PushAsync()
    {
        if (SelectedPcItem is null)
            return;

        var remoteName = Path.GetFileName(
            SelectedPcItem.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(remoteName))
            remoteName = SelectedPcItem.Name;

        var dest = $"{AndroidCurrentPath.TrimEnd('/')}/{remoteName}";
        var label = SelectedPcItem.IsDirectory ? "Klasör gönderiliyor" : "Gönderiliyor";
        await RunTransferAsync(
            () => _fileService.PushAsync(
                SelectedPcItem.FullPath,
                dest,
                new Progress<TransferProgress>(p => CurrentProgress = p)),
            $"{label}: {remoteName}",
            SelectedPcItem.IsDirectory ? "Klasör gönderme tamamlandı." : "Gönderme tamamlandı.");

        await NavigateAndroidAsync(AndroidCurrentPath);
    }

    [RelayCommand]
    private async Task DropToAndroidAsync(DragEventArgs? e)
    {
        if (e is null || !e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
            return;

        foreach (var file in files)
        {
            if (AndroidPackageFormats.IsInstallablePackage(file) && File.Exists(file))
            {
                IsTransferring = true;
                CurrentProgress = new TransferProgress { TotalBytes = 100, BytesTransferred = 0 };
                StatusMessage = $"Paket yükleniyor: {Path.GetFileName(file)}";
                try
                {
                    var result = await _appService.InstallApkAsync(
                        file,
                        new Progress<int>(p =>
                            CurrentProgress = new TransferProgress
                            {
                                TotalBytes = 100,
                                BytesTransferred = Math.Clamp(p, 0, 100)
                            }));
                    StatusMessage = result.Success
                        ? $"{Path.GetFileName(file)} yüklendi"
                        : $"Paket yükleme hatası: {result.Message}";
                    if (!result.Success)
                        await _dialogs.ShowMessageAsync("Paket Yükleme", StatusMessage);
                }
                catch (Exception ex)
                {
                    StatusMessage = ex.Message;
                    await _dialogs.ShowMessageAsync("Paket Yükleme", ex.Message);
                }
                finally
                {
                    IsTransferring = false;
                }

                continue;
            }

            var dest = $"{AndroidCurrentPath.TrimEnd('/')}/{Path.GetFileName(file)}";
            var isDir = Directory.Exists(file);
            await RunTransferAsync(
                () => _fileService.PushAsync(
                    file,
                    dest,
                    new Progress<TransferProgress>(p => CurrentProgress = p)),
                isDir ? $"Klasör gönderiliyor: {Path.GetFileName(file)}" : $"Gönderiliyor: {Path.GetFileName(file)}",
                isDir ? $"{Path.GetFileName(file)} klasörü gönderildi." : $"{Path.GetFileName(file)} gönderildi.");
        }

        await NavigateAndroidAsync(AndroidCurrentPath);
    }

    [RelayCommand]
    private async Task DropToPcAsync(DragEventArgs? e)
    {
        if (e?.Data.GetData(typeof(List<FileItemViewModel>)) is not List<FileItemViewModel> items)
            return;

        if (IsPcDrivesRoot(PcCurrentPath))
        {
            StatusMessage = "İndirmek için önce bir sürücü veya klasör açın.";
            return;
        }

        foreach (var item in items)
        {
            var dest = Path.Combine(PcCurrentPath, item.Name);
            await RunTransferAsync(
                () => _fileService.PullAsync(
                    item.FullPath,
                    dest,
                    new Progress<TransferProgress>(p => CurrentProgress = p)),
                item.IsDirectory ? $"Klasör indiriliyor: {item.Name}" : $"İndiriliyor: {item.Name}",
                item.IsDirectory ? $"{item.Name} klasörü indirildi." : $"{item.Name} indirildi.");
        }

        await NavigatePcAsync(PcCurrentPath);
    }

    [RelayCommand]
    private async Task DeleteAndroidItemAsync()
    {
        if (SelectedAndroidItem is null)
            return;

        var ok = await _dialogs.ShowConfirmationAsync(
            "Sil",
            $"'{SelectedAndroidItem.Name}' silinsin mi?");
        if (!ok) return;

        try
        {
            await _fileService.DeleteAsync(SelectedAndroidItem.FullPath);
            await NavigateAndroidAsync(AndroidCurrentPath);
            StatusMessage = "Silindi.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Hata: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CreateFolderAsync()
    {
        var name = $"YeniKlasor_{DateTime.Now:HHmmss}";
        await _fileService.CreateDirectoryAsync($"{AndroidCurrentPath.TrimEnd('/')}/{name}");
        await NavigateAndroidAsync(AndroidCurrentPath);
    }

    [RelayCommand]
    private void Rename()
    {
        if (SelectedAndroidItem is null) return;
        SelectedAndroidItem.IsRenaming = true;
        SelectedAndroidItem.EditName = SelectedAndroidItem.Name;
    }

    [RelayCommand]
    private async Task CommitRenameAsync(FileItemViewModel? item)
    {
        if (item is null) return;
        item.IsRenaming = false;
        if (string.IsNullOrWhiteSpace(item.EditName) || item.EditName == item.Name)
            return;

        await _fileService.RenameAsync(item.FullPath, item.EditName.Trim());
        await NavigateAndroidAsync(AndroidCurrentPath);
    }

    private bool CanTransfer() => !IsTransferring;

    private static bool IsPcDrivesRoot(string? path) =>
        string.IsNullOrWhiteSpace(path) ||
        string.Equals(path, PcDrivesRoot, StringComparison.OrdinalIgnoreCase);

    partial void OnIsTransferringChanged(bool value)
    {
        PullCommand.NotifyCanExecuteChanged();
        PushCommand.NotifyCanExecuteChanged();
    }

    private async Task RunTransferAsync(Func<Task> action, string startMsg, string endMsg)
    {
        IsTransferring = true;
        StatusMessage = startMsg;
        CurrentProgress = new TransferProgress
        {
            FileName = startMsg,
            BytesTransferred = 0,
            TotalBytes = 0
        };
        try
        {
            await action();
            StatusMessage = endMsg;
            if (CurrentProgress.TotalBytes <= 0 || CurrentProgress.Percentage < 100)
            {
                CurrentProgress = new TransferProgress
                {
                    FileName = endMsg,
                    BytesTransferred = 1,
                    TotalBytes = 1
                };
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Transfer hatası");
            StatusMessage = $"Hata: {ex.Message}";
            await _dialogs.ShowMessageAsync("Dosya Transferi", ex.Message);
        }
        finally
        {
            IsTransferring = false;
        }
    }

    private void BuildAndroidBreadcrumb(string path)
    {
        AndroidBreadcrumb.Clear();
        AndroidBreadcrumb.Add(new BreadcrumbItem("/", "/"));
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var built = new StringBuilder();
        foreach (var part in parts)
        {
            built.Append('/').Append(part);
            AndroidBreadcrumb.Add(new BreadcrumbItem(part, built.ToString()));
        }
    }
}
