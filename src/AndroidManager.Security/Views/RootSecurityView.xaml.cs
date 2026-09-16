using System.Windows;
using System.Windows.Controls;
using AndroidManager.Security.ViewModels;
using Prism.Navigation.Regions;

namespace AndroidManager.Security.Views;

public partial class RootSecurityView : UserControl, IRegionMemberLifetime
{
    public bool KeepAlive => false;

    public RootSecurityView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is RootSecurityViewModel vm)
            await vm.InitializeAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is IDisposable disposable)
            disposable.Dispose();
    }
}
