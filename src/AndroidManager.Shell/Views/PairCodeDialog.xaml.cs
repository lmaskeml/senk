using System.Windows;
using AndroidManager.Shell.ViewModels;

namespace AndroidManager.Shell.Views;

public partial class PairCodeDialog : Window
{
    private readonly PairCodeDialogViewModel _viewModel;

    public string PairCode => _viewModel.EnteredCode;

    public PairCodeDialog(string deviceName)
    {
        InitializeComponent();
        _viewModel = new PairCodeDialogViewModel(deviceName);
        DataContext = _viewModel;
        _viewModel.Confirmed += () =>
        {
            try { DialogResult = true; }
            catch { Close(); }
        };
        _viewModel.Cancelled += () =>
        {
            try { DialogResult = false; }
            catch { Close(); }
        };
        Loaded += (_, _) => Box1.Focus();
    }
}
