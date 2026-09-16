using System.Windows;
using System.Windows.Controls;
using AndroidManager.Transfer.ViewModels;

namespace AndroidManager.Transfer.Views;

public partial class TransferWizardView : UserControl
{
    public TransferWizardView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (DataContext is TransferWizardViewModel vm)
            await vm.InitializeAsync();
    }
}
