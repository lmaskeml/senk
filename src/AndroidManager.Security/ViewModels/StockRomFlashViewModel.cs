using System.Text;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AndroidManager.Security.ViewModels;

public sealed partial class StockRomFlashViewModel : ObservableObject
{
    private readonly IStockRomFlashService _flash;
    private readonly IAppDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly StringBuilder _logBuf = new(16_384);

    [ObservableProperty] private StockRomFolderInfo? _folder;
    [ObservableProperty] private string _folderPath = "";
    [ObservableProperty] private string _statusMessage = "Xiaomi fastboot ROM klasörünü seçin.";
    [ObservableProperty] private string _activityLog = "";
    [ObservableProperty] private bool _isBusy;

    public StockRomFlashViewModel(
        IStockRomFlashService flash,
        IAppDialogService dialogs,
        IUiDispatcher dispatcher,
        ILogger? logger = null)
    {
        _flash = flash;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _logger = logger ?? Log.ForContext<StockRomFlashViewModel>();
    }

    public Task InitializeAsync()
    {
        StatusMessage = "Xiaomi stock fastboot ROM klasörü seçin (içinde images\\boot.img olmalı).";
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var path = await _dialogs.PickFolderAsync("Orijinal ROM klasörü");
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            FolderPath = path;
            Folder = _flash.StageScripts(path);
            StatusMessage = Folder.IsValid
                ? "Klasör hazır. flash_all.bat ve inject-twrp.sh kopyalandı."
                : "images\\boot.img veya super.img eksik. Doğru Xiaomi fastboot klasörünü seçin.";
            AppendLog($"Klasör: {Folder.FolderPath}");
            AppendLog($"boot={Folder.HasBoot} super={Folder.HasSuper} twrp={Folder.HasTwrp} magisk={Folder.HasMagisk}");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[StockRom] Klasör açılamadı");
            StatusMessage = "Klasör okunamadı: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task PickTwrpAsync()
    {
        if (Folder is null)
        {
            await _dialogs.ShowMessageAsync("TWRP", "Önce orijinal ROM klasörünü seçin.");
            return;
        }

        var files = await _dialogs.PickOpenFilesAsync("TWRP imajı", "TWRP|*.img", multiSelect: false);
        var src = files?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(src))
            return;

        Folder = _flash.CopyTwrpImage(Folder.FolderPath, src);
        StatusMessage = "TWRP images\\twrp.img olarak kopyalandı.";
        AppendLog("TWRP kopyalandı: " + src);
    }

    [RelayCommand]
    private async Task PickMagiskAsync()
    {
        if (Folder is null)
        {
            await _dialogs.ShowMessageAsync("Magisk", "Önce orijinal ROM klasörünü seçin.");
            return;
        }

        var files = await _dialogs.PickOpenFilesAsync("Magisk zip", "Magisk|*.zip", multiSelect: false);
        var src = files?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(src))
            return;

        Folder = _flash.CopyMagiskZip(Folder.FolderPath, src);
        StatusMessage = "Magisk zip ROM klasörüne kopyalandı.";
        AppendLog("Magisk kopyalandı: " + src);
    }

    private bool CanStartFlash() => !IsBusy && Folder is { IsValid: true };

    [RelayCommand(CanExecute = nameof(CanStartFlash))]
    private async Task StartFlashAsync()
    {
        if (Folder is null)
            return;

        var ok = await _dialogs.ShowConfirmationAsync(
            "Orijinal ROM yükle",
            "Telefon fastboot'ta olmalı. userdata silinir (format).\n\n" +
            "Faz 1: stock partition flash\n" +
            "Faz 2: TWRP RAM boot + ramdisk inject + Magisk (varsa) + format data\n\n" +
            "Devam edilsin mi?");
        if (!ok)
            return;

        IsBusy = true;
        StatusMessage = "Yükleme başladı…";
        try
        {
            var progress = new Progress<string>(AppendLog);
            var result = await _flash.FlashAsync(Folder.FolderPath, progress);
            StatusMessage = result.Message;
            AppendLog(result.Message);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[StockRom] Flash başarısız");
            StatusMessage = "Hata: " + ex.Message;
            AppendLog(StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnIsBusyChanged(bool value) => StartFlashCommand.NotifyCanExecuteChanged();

    partial void OnFolderChanged(StockRomFolderInfo? value) => StartFlashCommand.NotifyCanExecuteChanged();

    private void AppendLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        var stamp = DateTime.Now.ToString("HH:mm:ss");
        var text = line.Trim();
        _dispatcher.Observe(() =>
        {
            if (_logBuf.Length > 24_000)
                _logBuf.Remove(0, _logBuf.Length - 16_000);
            _logBuf.Append('[').Append(stamp).Append("] ").Append(text).AppendLine();
            ActivityLog = _logBuf.ToString();
        });
    }
}
