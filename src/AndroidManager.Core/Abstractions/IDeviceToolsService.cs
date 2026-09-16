using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface IDeviceToolsService
{
    Task<DeviceToolResult> RebootAsync(DeviceRebootMode mode, CancellationToken cancellationToken = default);
    Task<DeviceToolResult> LockScreenAsync(CancellationToken cancellationToken = default);
    Task<DeviceToolResult> SetStayAwakeAsync(bool enabled, CancellationToken cancellationToken = default);
    Task<DeviceToolResult> SetWifiEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
    Task<DeviceToolResult> SetBluetoothEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
    Task<DeviceToolResult> CaptureScreenshotAsync(string? saveDirectory = null, CancellationToken cancellationToken = default);
    Task<DeviceToolResult> GetClipboardAsync(CancellationToken cancellationToken = default);
    Task<DeviceToolResult> SetClipboardAsync(string text, CancellationToken cancellationToken = default);
    Task<LiveDeviceMetrics> GetLiveMetricsAsync(CancellationToken cancellationToken = default);
}

public enum DeviceRebootMode
{
    System,
    Recovery,
    Bootloader
}

public sealed class DeviceToolResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string? PathOrPayload { get; init; }
}
