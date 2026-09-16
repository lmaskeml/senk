using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Microsoft.Toolkit.Uwp.Notifications;
using Serilog;
using Windows.UI.Notifications;

namespace AndroidManager.Shell.Services;

public sealed class NotificationService : INotificationService
{
    private readonly ISettingsService _settings;

    public NotificationService(ISettingsService settings)
    {
        _settings = settings;
    }

    public void ShowDeviceConnected(string deviceModel)
    {
        if (!CanNotify(s => s.NotifyOnDeviceConnect)) return;
        Send("Cihaz Bağlandı", deviceModel);
    }

    public void ShowDeviceDisconnected(string deviceModel)
    {
        if (!CanNotify(s => s.NotifyOnDeviceConnect)) return;
        Send("Cihaz Bağlantısı Kesildi", deviceModel);
    }

    public void ShowTransferComplete(string fileName, bool success, string? path = null)
    {
        if (!CanNotify(s => s.NotifyOnTransferComplete)) return;

        var title = success ? "Transfer tamamlandı" : "Transfer başarısız";
        try
        {
            var builder = new ToastContentBuilder()
                .AddText(title)
                .AddText(fileName);

            if (success && !string.IsNullOrWhiteSpace(path))
            {
                builder.AddButton(new ToastButton()
                    .SetContent("Klasörde Göster")
                    .AddArgument("action", "openFolder")
                    .AddArgument("path", path));
            }

            Push(builder);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Toast bildirimi gönderilemedi");
        }
    }

    public void ShowBackupComplete(string deviceModel, string size, bool success)
    {
        if (!CanNotify(s => s.NotifyOnBackupComplete)) return;
        var title = success ? "Yedekleme tamamlandı" : "Yedekleme başarısız";
        Send(title, $"{deviceModel} — {size}");
    }

    public void ShowError(string title, string message)
    {
        if (!_settings.Current.NotificationsEnabled) return;
        Send(title, message);
    }

    public void ShowInfo(string title, string message)
    {
        if (!_settings.Current.NotificationsEnabled) return;
        Send(title, message);
    }

    private static void Send(string title, string body)
    {
        try
        {
            Push(new ToastContentBuilder().AddText(title).AddText(body));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Toast bildirimi gönderilemedi: {Title}", title);
        }
    }

    private static void Push(ToastContentBuilder builder)
    {
        // Avoid Prism's IDialogService.Show extension colliding with Toolkit's Show().
        var content = builder.GetToastContent();
        var toast = new ToastNotification(content.GetXml())
        {
            ExpirationTime = DateTimeOffset.Now.AddMinutes(5)
        };
        ToastNotificationManagerCompat.CreateToastNotifier().Show(toast);
    }

    private bool CanNotify(Func<AppSettings, bool> check) =>
        _settings.Current.NotificationsEnabled && check(_settings.Current);
}
