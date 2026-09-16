using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AndroidManager.Messages.ViewModels;

public sealed partial class WhatsAppWebViewModel : ObservableObject
{
    [ObservableProperty] private string _statusMessage = "WhatsApp Web hazırlanıyor…";
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private bool _hasSession;
    [ObservableProperty] private string _sessionInfo = string.Empty;
    [ObservableProperty] private double _zoomFactor = 1.0;

    public event Func<Task>? ReloadRequested;
    public event Func<Task>? ClearSessionRequested;
    public event Action<double>? ZoomRequested;

    public void MarkReady(bool hasSession = false, string? sessionInfo = null)
    {
        IsLoading = false;
        IsReady = true;
        HasSession = hasSession;
        if (!string.IsNullOrWhiteSpace(sessionInfo))
            SessionInfo = sessionInfo;
        StatusMessage = hasSession
            ? "WhatsApp Web — kayıtlı oturum yüklendi"
            : "WhatsApp Web — QR ile bağlanın veya kayıtlı oturum yüklenir";
    }

    public void MarkLoading(string message)
    {
        IsLoading = true;
        StatusMessage = message;
    }

    public void MarkError(string message)
    {
        IsLoading = false;
        IsReady = false;
        StatusMessage = message;
        Log.Warning("WhatsApp Web: {Message}", message);
    }

    public void UpdateSession(bool hasSession, string info)
    {
        HasSession = hasSession;
        SessionInfo = info;
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (ReloadRequested is null) return;
        await ReloadRequested.Invoke();
    }

    [RelayCommand]
    private async Task ClearSessionAsync()
    {
        if (ClearSessionRequested is null) return;
        await ClearSessionRequested.Invoke();
        HasSession = false;
        SessionInfo = string.Empty;
    }

    [RelayCommand]
    private void ZoomIn()
    {
        ZoomFactor = Math.Min(ZoomFactor + 0.1, 2.0);
        ZoomRequested?.Invoke(ZoomFactor);
    }

    [RelayCommand]
    private void ZoomOut()
    {
        ZoomFactor = Math.Max(ZoomFactor - 0.1, 0.5);
        ZoomRequested?.Invoke(ZoomFactor);
    }

    [RelayCommand]
    private void OpenInBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = WhatsAppWebPaths.WebUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MarkError($"Tarayıcı açılamadı: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenCacheFolder()
    {
        try
        {
            var dir = WhatsAppWebPaths.UserDataDirectory;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MarkError($"Önbellek klasörü açılamadı: {ex.Message}");
        }
    }
}
