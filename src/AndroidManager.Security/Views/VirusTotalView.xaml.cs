using System.Windows;
using System.Windows.Controls;
using AndroidManager.Security.ViewModels;
using Prism.Navigation.Regions;

namespace AndroidManager.Security.Views;

public partial class VirusTotalView : UserControl, IRegionMemberLifetime
{
    public bool KeepAlive => false;

    public VirusTotalView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is VirusTotalViewModel vm)
        {
            await vm.InitializeAsync();
            if (!string.IsNullOrEmpty(vm.ApiKey))
                ApiKeyBox.Password = vm.ApiKey;
        }
    }

    private void ApiKeyBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is VirusTotalViewModel vm)
            vm.ApiKey = ApiKeyBox.Password;
    }
}
