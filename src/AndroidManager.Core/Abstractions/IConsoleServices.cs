using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface IAdbTerminalService : IAsyncDisposable
{
    bool IsSessionOpen { get; }
    event EventHandler<string>? OutputReceived;
    event EventHandler? SessionClosed;

    Task OpenSessionAsync(CancellationToken cancellationToken = default);
    Task CloseSessionAsync();
    Task WriteLineAsync(string line, CancellationToken cancellationToken = default);
    Task<string> ExecuteOnceAsync(string command, CancellationToken cancellationToken = default);
}

public interface ILogcatService : IAsyncDisposable
{
    bool IsRunning { get; }
    event EventHandler<LogcatLine>? LineReceived;
    event EventHandler? Stopped;

    Task StartAsync(LogcatStartOptions options, CancellationToken cancellationToken = default);
    Task StopAsync();
    Task ClearDeviceLogAsync(CancellationToken cancellationToken = default);
}

public interface IBootControlService
{
    Task<BootStatusInfo> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<DeviceToolResult> RebootAsync(DeviceRebootMode mode, CancellationToken cancellationToken = default);
    Task<DeviceToolResult> PowerOffAsync(CancellationToken cancellationToken = default);
    Task<DeviceToolResult> SoftRebootAsync(CancellationToken cancellationToken = default);
}
