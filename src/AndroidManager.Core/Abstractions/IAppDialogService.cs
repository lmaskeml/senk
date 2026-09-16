namespace AndroidManager.Core.Abstractions;

public interface IAppDialogService
{
    Task<bool> ShowConfirmationAsync(string title, string message);
    Task ShowMessageAsync(string title, string message);

    /// <summary>
    /// Opens WiFi connect UI (auto discovery / manual IP / QR).
    /// Returns null if cancelled; otherwise connection attempt result.
    /// </summary>
    Task<WifiConnectSessionResult?> ShowWifiConnectDialogAsync();

    Task<string?> PromptTextAsync(string title, string message, string? defaultValue = null);

    /// <summary>6-digit companion pair code entry UI. Null if cancelled.</summary>
    Task<string?> PromptPairCodeAsync(string deviceName);
    Task<string?> PickFolderAsync(string title);
    Task<IReadOnlyList<string>?> PickOpenFilesAsync(string title, string filter, bool multiSelect = false);
    Task<string?> PickSaveFileAsync(string title, string filter, string? defaultFileName = null);
}

/// <summary>Legacy shape kept for callers that only need IP/port.</summary>
public sealed class WifiConnectRequest
{
    public required string IpAddress { get; init; }
    public int Port { get; init; } = 5555;
}

public sealed class WifiConnectSessionResult
{
    public bool Success { get; init; }
    public string IpAddress { get; init; } = string.Empty;
    public int Port { get; init; } = 5555;
    public string Message { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;
}
