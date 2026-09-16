using AndroidManager.Core.Events;
using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface IAdbService : IAsyncDisposable
{
    ConnectedDevice? SelectedDevice { get; }
    IReadOnlyList<ConnectedDevice> Devices { get; }
    bool IsRunning { get; }

    event EventHandler<DeviceConnectionChangedEventArgs>? DeviceConnectionChanged;
    event EventHandler<ConnectedDevice?>? SelectedDeviceChanged;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ConnectedDevice>> GetDevicesAsync(CancellationToken cancellationToken = default);
    void SelectDevice(ConnectedDevice? device);
    Task<string> ExecuteShellAsync(string command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exclusive ADB channel access (shell / sync / CLI). Nested calls from within the action must not wait again.
    /// </summary>
    Task RunExclusiveAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default);

    Task<bool> ConnectWifiAsync(string ipAddress, int port = 5555, CancellationToken cancellationToken = default);
    Task<bool> PairWifiAsync(string ipAddress, int port, string pairingCode, CancellationToken cancellationToken = default);
    /// <summary>Host-side <c>adb install-multiple -r</c> for split APK sets.</summary>
    Task<InstallResult> InstallLocalPackagesAsync(
        IReadOnlyList<string> localApkPaths,
        CancellationToken cancellationToken = default);

    /// <summary>Host-side <c>adb sideload</c> for recovery packages. Streams percent via <paramref name="progress"/>.</summary>
    Task<DeviceToolResult> SideloadAsync(
        string zipPath,
        IProgress<SideloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Raw <c>adb exec-out</c> into a local file. Use for <c>dd</c> block dumps — never <c>adb shell</c> (PTY corrupts binary).
    /// </summary>
    Task<BlockDumpResult> ExecOutToFileAsync(
        string remoteCommand,
        string localFilePath,
        long expectedBytes = 0,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task EnableTcpipAsync(int port = 5555, CancellationToken cancellationToken = default);
    Task<string?> GetDeviceIpAsync(CancellationToken cancellationToken = default);
    Task<UsbWifiSwitchResult> SwitchUsbToWifiAsync(int port = 5555, CancellationToken cancellationToken = default);

    /// <summary>Polls until a device is Online, Recovery, or Sideload. Null if timeout.</summary>
    Task<ConnectedDevice?> WaitForAdbDeviceAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>Host-side <c>adb reboot [bootloader|recovery]</c> — sideload modunda shell reboot çalışmaz.</summary>
    Task<DeviceToolResult> RebootTransportAsync(
        string? target,
        string? serial = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Host <c>adb</c> CLI via <see cref="RunExclusiveAsync"/> channel (TWRP push/shell, flash yardımcıları).
    /// </summary>
    Task<(int ExitCode, string Output)> RunHostAdbAsync(
        string? serial,
        string arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>adb backup</c> with live status — blocks until the user confirms on device or timeout.
    /// Retries when the backup dialog was missed (process exits too quickly).
    /// </summary>
    Task<AdbAppBackupResult> RunInteractiveAppBackupAsync(
        string packageName,
        string outputAbPath,
        IProgress<AdbAppBackupProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
