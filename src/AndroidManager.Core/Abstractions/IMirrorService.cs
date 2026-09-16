using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface IMirrorService : IAsyncDisposable
{
    bool IsStreaming { get; }
    bool IsScrcpyAvailable { get; }

    /// <summary>PNG frame bytes for UI conversion (no WPF types in Core).</summary>
    event EventHandler<byte[]>? FrameReceived;
    event EventHandler<MirrorErrorEventArgs>? ErrorOccurred;

    Task StartScrcpyAsync(ScrcpyOptions options, CancellationToken cancellationToken = default);
    Task StartScreencapStreamAsync(int fps, CancellationToken cancellationToken = default);
    Task StopAsync();

    Task<byte[]?> CaptureScreenshotAsync(CancellationToken cancellationToken = default);
    Task SaveScreenshotAsync(string savePath, CancellationToken cancellationToken = default);

    Task SendTapAsync(int x, int y, CancellationToken cancellationToken = default);
    Task SendSwipeAsync(int x1, int y1, int x2, int y2, int durationMs = 300, CancellationToken cancellationToken = default);
    Task SendKeyEventAsync(AndroidKeyCode keyCode, CancellationToken cancellationToken = default);
    Task SendTextAsync(string text, CancellationToken cancellationToken = default);

    Task<DeviceResolution> GetDeviceResolutionAsync(CancellationToken cancellationToken = default);
}
