using System.Windows;
using System.Windows.Controls;
using AndroidManager.Backup.ViewModels;

namespace AndroidManager.Backup.Views;

public partial class BackupView : UserControl
{
    public BackupView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (DataContext is BackupViewModel vm)
            await vm.InitializeAsync();
    }
}
