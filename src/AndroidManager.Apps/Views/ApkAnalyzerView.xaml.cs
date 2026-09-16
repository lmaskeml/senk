using System.Windows;
using System.Windows.Controls;
using AndroidManager.Apps.ViewModels;
using Prism.Navigation.Regions;

namespace AndroidManager.Apps.Views;

public partial class ApkAnalyzerView : UserControl, IRegionMemberLifetime
{
    public bool KeepAlive => false;

    public ApkAnalyzerView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ApkAnalyzerViewModel vm)
            await vm.InitializeAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is IDisposable disposable)
            disposable.Dispose();
    }
}
