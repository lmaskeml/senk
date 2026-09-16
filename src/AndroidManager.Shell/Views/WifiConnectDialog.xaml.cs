using System.Globalization;
using System.Windows;
using AndroidManager.Core.Abstractions;
using AndroidManager.Shell.ViewModels;

namespace AndroidManager.Shell.Views;

public partial class WifiConnectDialog : Window
{
    private readonly WifiConnectViewModel _viewModel;

    public WifiConnectSessionResult? SessionResult => _viewModel.Result;

    public WifiConnectDialog(WifiConnectViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        _viewModel.CloseAccepted = () =>
        {
            try { DialogResult = true; }
            catch { Close(); }
        };
        _viewModel.CloseCancelled = () =>
        {
            try { DialogResult = false; }
            catch { Close(); }
        };
        Loaded += async (_, _) =>
        {
            try
            {
                await _viewModel.OnOpenedAsync();
            }
            catch (System.Exception ex)
            {
                Serilog.Log.Error(ex, "Error in WifiConnectDialog Loaded event handler");
            }
        };
        Closed += (_, _) => _viewModel.Dispose();
    }
}
