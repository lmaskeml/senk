using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AndroidManager.Device.ViewModels;
using Prism.Navigation.Regions;

namespace AndroidManager.Device.Views;

public partial class TerminalView : UserControl, IRegionMemberLifetime
{
    public bool KeepAlive => false;

    public TerminalView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += (_, _) => HookOutput();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is TerminalViewModel vm)
            await vm.InitializeAsync();
        HookOutput();
        CommandBox.Focus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is IDisposable disposable)
            disposable.Dispose();
    }

    private void HookOutput()
    {
        if (DataContext is not TerminalViewModel vm)
            return;

        vm.PropertyChanged -= VmOnPropertyChanged;
        vm.PropertyChanged += VmOnPropertyChanged;
    }

    private void VmOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalViewModel.OutputText))
            OutputScroll.ScrollToEnd();
    }

    private void CommandBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not TerminalViewModel vm)
            return;

        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (vm.SendCommand.CanExecute(null))
                vm.SendCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            vm.HistoryUp();
            CommandBox.CaretIndex = CommandBox.Text.Length;
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            vm.HistoryDown();
            CommandBox.CaretIndex = CommandBox.Text.Length;
            e.Handled = true;
        }
    }
}
