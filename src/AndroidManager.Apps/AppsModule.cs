using AndroidManager.Apps.Services;
using AndroidManager.Apps.ViewModels;
using AndroidManager.Apps.Views;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Navigation;
using Prism.Ioc;
using Prism.Modularity;

namespace AndroidManager.Apps;

public sealed class AppsModule : IModule
{
    public void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry.RegisterSingleton<IAppService, AppService>();
        containerRegistry.RegisterSingleton<IDebloaterService, DebloaterService>();
        containerRegistry.RegisterSingleton<IApkAnalyzerService, ApkAnalyzerService>();
        containerRegistry.RegisterForNavigation<AppManagerView, AppManagerViewModel>(ViewNames.Apps);
        containerRegistry.RegisterForNavigation<ApkAnalyzerView, ApkAnalyzerViewModel>(ViewNames.ApkAnalyzer);
    }

    public void OnInitialized(IContainerProvider containerProvider)
    {
    }
}
