using System.Windows;
using System.Windows.Controls;
using AndroidManager.Messages.ViewModels;
using Prism.Navigation.Regions;

namespace AndroidManager.Messages.Views;

public partial class SmsView : UserControl, IRegionMemberLifetime
{
    public bool KeepAlive => false;

    public SmsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is SmsViewModel vm)
            await vm.InitializeAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is IDisposable disposable)
            disposable.Dispose();
    }
}
