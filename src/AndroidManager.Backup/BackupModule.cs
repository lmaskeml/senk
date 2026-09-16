using System.IO;
using AndroidManager.Backup.Data;
using AndroidManager.Backup.Services;
using AndroidManager.Backup.ViewModels;
using AndroidManager.Backup.Views;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Navigation;
using AndroidManager.Core.Services;
using Prism.Ioc;
using Prism.Modularity;

namespace AndroidManager.Backup;

public sealed class BackupModule : IModule
{
    public void RegisterTypes(IContainerRegistry containerRegistry)
    {
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AndroidManager",
            "backup_history.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        containerRegistry.RegisterInstance(new BackupRepository(dbPath));
        // Security modülü yüklenince RootElevatedShellService ile değiştirilir.
        if (!containerRegistry.IsRegistered<IElevatedShellService>())
            containerRegistry.RegisterSingleton<IElevatedShellService, NullElevatedShellService>();
        containerRegistry.RegisterSingleton<IBackupService, BackupService>();
        containerRegistry.RegisterForNavigation<BackupView, BackupViewModel>(ViewNames.Backup);
    }

    public void OnInitialized(IContainerProvider containerProvider)
    {
    }
}
