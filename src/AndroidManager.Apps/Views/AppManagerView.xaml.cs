using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AndroidManager.Apps.ViewModels;
using AndroidManager.Core.Models;

namespace AndroidManager.Apps.Views;

public partial class AppManagerView : UserControl
{
    public AppManagerView()
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
        if (DataContext is AppManagerViewModel vm)
            await vm.InitializeAsync();
    }

    private void AppsList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is AppManagerViewModel vm && vm.SelectedApp is not null)
            vm.LoadIconCommand.Execute(vm.SelectedApp);
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasInstallablePackages(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is AppManagerViewModel vm && vm.DropPackagesCommand.CanExecute(e))
            vm.DropPackagesCommand.Execute(e);
        e.Handled = true;
    }

    private static bool HasInstallablePackages(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return false;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
            return false;
        return files.Any(AndroidPackageFormats.IsInstallablePackage);
    }
}
