using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Navigation;
using AndroidManager.Settings.Services;
using AndroidManager.Settings.ViewModels;
using AndroidManager.Settings.Views;
using Prism.Ioc;
using Prism.Modularity;

namespace AndroidManager.Settings;

public sealed class SettingsModule : IModule
{
    public void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry.RegisterSingleton<ISettingsService, SettingsService>();
        containerRegistry.RegisterForNavigation<SettingsView, SettingsViewModel>(ViewNames.Settings);
    }

    public void OnInitialized(IContainerProvider containerProvider)
    {
        var settings = containerProvider.Resolve<ISettingsService>();
        ThemeHelper.Apply(settings.Current.Theme, settings.Current.AccentName, settings.Current.FontSize);
    }
}
