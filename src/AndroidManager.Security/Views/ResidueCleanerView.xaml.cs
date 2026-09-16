using System.Windows;
using System.Windows.Controls;
using AndroidManager.Security.ViewModels;
using Prism.Navigation.Regions;

namespace AndroidManager.Security.Views;

public partial class ResidueCleanerView : UserControl, IRegionMemberLifetime
{
    public bool KeepAlive => false;

    public ResidueCleanerView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ResidueCleanerViewModel vm)
            await vm.InitializeAsync();
    }
}
