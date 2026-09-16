using System.Windows;
using System.Windows.Controls;
using AndroidManager.Security.ViewModels;
using Prism.Navigation.Regions;

namespace AndroidManager.Security.Views;

public partial class CustomRomWizardView : UserControl, IRegionMemberLifetime
{
    public bool KeepAlive => false;

    public CustomRomWizardView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        AllowDrop = true;
        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (DataContext is CustomRomWizardViewModel vm)
            await vm.InitializeAsync();
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasRomZip(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var path = GetRomZip(e);
        if (path is not null
            && DataContext is CustomRomWizardViewModel vm
            && vm.LoadPackageCommand.CanExecute(path))
        {
            vm.LoadPackageCommand.Execute(path);
        }

        e.Handled = true;
    }

    private static bool HasRomZip(DragEventArgs e) => GetRomZip(e) is not null;

    private static string? GetRomZip(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
            return null;

        return files.FirstOrDefault(f =>
            File.Exists(f) && f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }
}
