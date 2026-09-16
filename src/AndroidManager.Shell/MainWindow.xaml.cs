using System.ComponentModel;
using System.Windows;
using AndroidManager.Settings;
using AndroidManager.Shell.ViewModels;
using MaterialDesignThemes.Wpf;

namespace AndroidManager.Shell;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplySidebarWidth(viewModel.IsSidebarCollapsed);
        Loaded += async (_, _) => await viewModel.InitializeAsync();
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.Dispose();
        };
    }

    private void OnSourceInitialized(object sender, EventArgs e) => SyncChrome();

    private void OnWindowStateChanged(object sender, EventArgs e) => SyncChrome();

    private void SyncChrome()
    {
        var maximized = WindowState == WindowState.Maximized;
        BorderThickness = maximized ? new Thickness(8) : new Thickness(0);
        MaximizeIcon.Kind = maximized ? PackIconKind.WindowRestore : PackIconKind.WindowMaximize;
        MaximizeButton.ToolTip = maximized ? "Geri yükle" : "Büyüt";
        DwmWindowFrame.Apply(this);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsSidebarCollapsed))
            ApplySidebarWidth(_viewModel.IsSidebarCollapsed);
    }

    private void ApplySidebarWidth(bool collapsed)
    {
        SidebarColumn.Width = new GridLength(collapsed ? 72 : 248);
    }
}
