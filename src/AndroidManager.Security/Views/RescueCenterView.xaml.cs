using System.Windows;
using System.Windows.Controls;
using AndroidManager.Security.ViewModels;
using Prism.Navigation.Regions;

namespace AndroidManager.Security.Views;

public partial class RescueCenterView : UserControl, IRegionMemberLifetime
{
    public bool KeepAlive => false;

    public RescueCenterView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is RescueCenterViewModel vm)
            await vm.InitializeAsync();
    }
}
