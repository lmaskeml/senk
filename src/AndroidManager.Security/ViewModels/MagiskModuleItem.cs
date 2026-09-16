using AndroidManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AndroidManager.Security.ViewModels;

public sealed class MagiskModuleItem : ObservableObject
{
    public MagiskModuleItem(MagiskModuleInfo info)
    {
        Info = info;
    }

    public MagiskModuleInfo Info { get; }

    public string Id => Info.Id;
    public string Name => Info.DisplayName;
    public string Version => string.IsNullOrWhiteSpace(Info.Version) ? "—" : Info.Version;
    public string Author => string.IsNullOrWhiteSpace(Info.Author) ? "—" : Info.Author;
    public string Description => Info.Description;
    public bool IsEnabled => Info.IsEnabled;
    public bool RemovePending => Info.RemovePending;
    public string StatusLabel => Info.StatusLabel;
    public string StatusColor => Info.StatusColor;
    public string ToggleLabel => Info.IsEnabled ? "Kapat" : "Aç";
    public bool CanToggle => !Info.RemovePending && !Info.InstallPending;
}
