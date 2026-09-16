using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Navigation;
using AndroidManager.Files.Services;
using AndroidManager.Files.ViewModels;
using AndroidManager.Files.Views;
using Prism.Ioc;
using Prism.Modularity;

namespace AndroidManager.Files;

public sealed class FilesModule : IModule
{
    public void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry.RegisterSingleton<IFileService, FileService>();
        containerRegistry.RegisterSingleton<IStorageAnalyzerService, StorageAnalyzerService>();
        containerRegistry.RegisterForNavigation<FileManagerView, FileManagerViewModel>(ViewNames.Files);
        containerRegistry.RegisterForNavigation<StorageAnalyzerView, StorageAnalyzerViewModel>(ViewNames.StorageAnalyzer);
    }

    public void OnInitialized(IContainerProvider containerProvider)
    {
    }
}
