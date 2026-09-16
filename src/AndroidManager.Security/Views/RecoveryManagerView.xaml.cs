using System.Windows;
using System.Windows.Controls;
using AndroidManager.Security.ViewModels;
using Prism.Navigation.Regions;

namespace AndroidManager.Security.Views;

public partial class RecoveryManagerView : UserControl, IRegionMemberLifetime
{
    public bool KeepAlive => false;

    public RecoveryManagerView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is RecoveryManagerViewModel vm)
            await vm.InitializeAsync();
    }
}
