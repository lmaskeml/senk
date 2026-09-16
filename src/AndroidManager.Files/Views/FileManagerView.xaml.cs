using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AndroidManager.Files.ViewModels;

namespace AndroidManager.Files.Views;

public partial class FileManagerView : UserControl
{
    public FileManagerView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (DataContext is FileManagerViewModel vm)
            await vm.InitializeAsync();
    }

    private void AndroidList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is FileManagerViewModel vm && vm.SelectedAndroidItem is not null)
            vm.OpenAndroidItemCommand.Execute(vm.SelectedAndroidItem);
    }

    private void PcList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is FileManagerViewModel vm && vm.SelectedPcItem is not null)
            vm.OpenPcItemCommand.Execute(vm.SelectedPcItem);
    }
}
