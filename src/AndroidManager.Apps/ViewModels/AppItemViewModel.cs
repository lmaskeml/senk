using System.Windows.Media.Imaging;
using AndroidManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AndroidManager.Apps.ViewModels;

public sealed partial class AppItemViewModel : ObservableObject
{
    public AndroidApp Model { get; }

    [ObservableProperty] private BitmapImage? _icon;
    [ObservableProperty] private bool _hasIcon;

    public string PackageName => Model.PackageName;
    public string AppName => string.IsNullOrWhiteSpace(Model.AppName) ? Model.PackageName : Model.AppName;
    public string VersionName => Model.VersionName;
    public string ApkSize => Model.ApkSizeFormatted;
    public bool IsSystemApp => Model.IsSystemApp;
    public string InstallDate => Model.InstallDate == default
        ? "-"
        : Model.InstallDate.ToString("yyyy-MM-dd");

    public string InitialLetter => AppName.Length > 0
        ? char.ToUpperInvariant(AppName[0]).ToString()
        : "?";

    public AppItemViewModel(AndroidApp model) => Model = model;
}
