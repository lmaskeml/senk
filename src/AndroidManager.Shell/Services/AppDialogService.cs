using System.Windows;
using AndroidManager.Core.Abstractions;
using AndroidManager.Shell.ViewModels;
using AndroidManager.Shell.Views;
using Microsoft.Win32;
using Prism.Ioc;

namespace AndroidManager.Shell.Services;

public sealed class AppDialogService : IAppDialogService
{
    private readonly IContainerProvider _container;
    private readonly IUiDispatcher _dispatcher;

    public AppDialogService(IContainerProvider container, IUiDispatcher dispatcher)
    {
        _container = container;
        _dispatcher = dispatcher;
    }

    public Task<bool> ShowConfirmationAsync(string title, string message) =>
        OnUi(() =>
        {
            var result = MessageBox.Show(
                GetOwner(),
                message,
                title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            return result == MessageBoxResult.Yes;
        });

    public Task ShowMessageAsync(string title, string message) =>
        OnUi(() =>
        {
            MessageBox.Show(
                GetOwner(),
                message,
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        });

    public Task<WifiConnectSessionResult?> ShowWifiConnectDialogAsync() =>
        OnUi(() =>
        {
            var vm = _container.Resolve<WifiConnectViewModel>();
            var dialog = new WifiConnectDialog(vm)
            {
                Owner = GetOwner()
            };

            var ok = dialog.ShowDialog() == true;
            return ok ? dialog.SessionResult : null;
        });

    public Task<string?> PromptTextAsync(string title, string message, string? defaultValue = null) =>
        OnUi(() =>
        {
            var dialog = new PromptTextDialog(title, message, defaultValue)
            {
                Owner = GetOwner()
            };
            return dialog.ShowDialog() == true ? dialog.Value : null;
        });

    public Task<string?> PromptPairCodeAsync(string deviceName) =>
        OnUi(() =>
        {
            var dialog = new PairCodeDialog(deviceName)
            {
                Owner = GetOwner()
            };
            return dialog.ShowDialog() == true ? dialog.PairCode : null;
        });

    public Task<string?> PickFolderAsync(string title) =>
        OnUi(() =>
        {
            var dialog = new OpenFolderDialog
            {
                Title = title
            };

            return dialog.ShowDialog() == true ? dialog.FolderName : null;
        });

    public Task<IReadOnlyList<string>?> PickOpenFilesAsync(string title, string filter, bool multiSelect = false) =>
        OnUi(() =>
        {
            var dialog = new OpenFileDialog
            {
                Title = title,
                Filter = filter,
                Multiselect = multiSelect
            };

            if (dialog.ShowDialog() != true)
                return null;

            IReadOnlyList<string> files = multiSelect ? dialog.FileNames : [dialog.FileName];
            return files;
        });

    public Task<string?> PickSaveFileAsync(string title, string filter, string? defaultFileName = null) =>
        OnUi(() =>
        {
            var dialog = new SaveFileDialog
            {
                Title = title,
                Filter = filter,
                FileName = defaultFileName ?? string.Empty
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        });

    private Task OnUi(Action action) => _dispatcher.InvokeAsync(action);

    private Task<T> OnUi<T>(Func<T> action) => _dispatcher.InvokeAsync(action);

    private static Window? GetOwner() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? Application.Current?.MainWindow;
}
