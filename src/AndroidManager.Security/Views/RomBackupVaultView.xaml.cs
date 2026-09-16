using System.Windows;
using System.Windows.Controls;
using AndroidManager.Security.ViewModels;
using Prism.Navigation.Regions;

namespace AndroidManager.Security.Views;

public partial class RomBackupVaultView : UserControl, IRegionMemberLifetime
{
    public bool KeepAlive => false;

    public RomBackupVaultView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (DataContext is RomBackupVaultViewModel vm)
            await vm.InitializeAsync();
    }
}
