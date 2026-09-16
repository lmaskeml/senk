using System.Windows;
using System.Windows.Controls;
using AndroidManager.Security.ViewModels;

namespace AndroidManager.Security.Views;

public partial class SecurityView : UserControl
{
    public SecurityView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is SecurityViewModel vm)
            await vm.InitializeAsync();
    }
}
