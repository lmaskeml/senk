using System.Windows;
using System.Windows.Controls;
using AndroidManager.Files.ViewModels;
using Prism.Navigation.Regions;

namespace AndroidManager.Files.Views;

public partial class StorageAnalyzerView : UserControl, IRegionMemberLifetime
{
    public bool KeepAlive => false;

    public StorageAnalyzerView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is StorageAnalyzerViewModel vm)
            await vm.InitializeAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is IDisposable disposable)
            disposable.Dispose();
    }
}
