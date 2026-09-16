using System.Windows;
using System.Windows.Controls;
using AndroidManager.Device.ViewModels;
using Prism.Navigation.Regions;

namespace AndroidManager.Device.Views;

public partial class BootControlView : UserControl, IRegionMemberLifetime
{
    public bool KeepAlive => false;

    public BootControlView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is BootControlViewModel vm)
            await vm.InitializeAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is IDisposable disposable)
            disposable.Dispose();
    }
}
