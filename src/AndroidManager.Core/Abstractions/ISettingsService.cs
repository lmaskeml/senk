using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface ISettingsService
{
    AppSettings Current { get; }
    void Save();
    void Reset();
    event EventHandler<AppSettings>? SettingsChanged;
}

public interface INotificationService
{
    void ShowDeviceConnected(string deviceModel);
    void ShowDeviceDisconnected(string deviceModel);
    void ShowTransferComplete(string fileName, bool success, string? path = null);
    void ShowBackupComplete(string deviceModel, string size, bool success);
    void ShowError(string title, string message);
    void ShowInfo(string title, string message);
}
