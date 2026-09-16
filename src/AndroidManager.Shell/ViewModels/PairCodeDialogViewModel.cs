using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AndroidManager.Shell.ViewModels;

public sealed partial class PairCodeDialogViewModel : ObservableObject
{
    public string DeviceName { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _enteredCode = string.Empty;

    [ObservableProperty] private string _errorMessage = string.Empty;

    [ObservableProperty] private string _d1 = string.Empty;
    [ObservableProperty] private string _d2 = string.Empty;
    [ObservableProperty] private string _d3 = string.Empty;
    [ObservableProperty] private string _d4 = string.Empty;
    [ObservableProperty] private string _d5 = string.Empty;
    [ObservableProperty] private string _d6 = string.Empty;

    public event Action? Confirmed;
    public event Action? Cancelled;

    public PairCodeDialogViewModel(string deviceName)
    {
        DeviceName = deviceName;
    }

    partial void OnD1Changed(string value) => RebuildCode();
    partial void OnD2Changed(string value) => RebuildCode();
    partial void OnD3Changed(string value) => RebuildCode();
    partial void OnD4Changed(string value) => RebuildCode();
    partial void OnD5Changed(string value) => RebuildCode();
    partial void OnD6Changed(string value) => RebuildCode();

    private void RebuildCode()
    {
        EnteredCode = $"{TakeDigit(D1)}{TakeDigit(D2)}{TakeDigit(D3)}{TakeDigit(D4)}{TakeDigit(D5)}{TakeDigit(D6)}";
        ErrorMessage = string.Empty;
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    private static string TakeDigit(string value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value[^1].ToString();

    private bool CanConfirm() =>
        EnteredCode.Length == 6 && EnteredCode.All(char.IsDigit);

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        if (!CanConfirm())
        {
            ErrorMessage = "6 haneli kod giriniz";
            return;
        }

        Confirmed?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke();
}
