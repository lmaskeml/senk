using System.IO;
using System.Windows;
using AndroidManager.Core.Models;
using CommunityToolkit.Mvvm.Input;

namespace AndroidManager.Apps.ViewModels;

// Drop handler partial methods live with AppManagerViewModel
public sealed partial class AppManagerViewModel
{
    [RelayCommand]
    private async Task DropPackagesAsync(DragEventArgs? e)
    {
        if (e is null || !e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
            return;

        var packages = files
            .Where(f => !Directory.Exists(f) && AndroidPackageFormats.IsInstallablePackage(f))
            .ToList();

        if (packages.Count == 0)
        {
            StatusMessage = "Sadece .apk / .xapk / .apks / .apkm bırakın";
            return;
        }

        foreach (var file in packages)
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

                if (!result.Success)
                    await _dialogs.ShowMessageAsync("Paket Yükleme", StatusMessage);
                else
                    await LoadAppsAsync();
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
                await _dialogs.ShowMessageAsync("Paket Yükleme", ex.Message);
            }
            finally
            {
                IsInstalling = false;
            }
        }
    }
}
