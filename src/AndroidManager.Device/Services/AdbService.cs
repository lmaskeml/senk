using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Events;
using AndroidManager.Core.Models;
using AndroidManager.Core.Services;
using AndroidManager.Device.Parsing;
using Serilog;

namespace AndroidManager.Device.Services;

public sealed class AdbService : IAdbService
{
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private readonly List<ConnectedDevice> _devices = [];
    private AdbClient? _adbClient;
    private DeviceMonitor? _deviceMonitor;
    private CancellationTokenSource? _monitorCts;
    private ConnectedDevice? _selectedDevice;
    private bool _isRunning;

    /// <summary>
    /// Tek ADB kanalı — shell + sync + CLI aynı anda Wi‑Fi'de timeout/kopma üretir.
    /// SyncService bu kapıyı tutarken <see cref="ExecuteShellUngatedAsync"/> kullanır (iç içe deadlock yok).
    /// </summary>
    /// <summary>ADB shell/sync/CLI — <see cref="DeviceTransportGate"/> ile fastboot paylaşılır.</summary>
    internal SemaphoreSlim DeviceChannel => DeviceTransportGate.Channel;

    public AdbService(ILogger? logger = null)
    {
        _logger = logger ?? Log.ForContext<AdbService>();
    }

    public ConnectedDevice? SelectedDevice
    {
        get
        {
            lock (_sync) return _selectedDevice;
        }
    }

    public IReadOnlyList<ConnectedDevice> Devices
    {
        get
        {
            lock (_sync) return _devices.ToList();
        }
    }

    public bool IsRunning => _isRunning;

    public event EventHandler<DeviceConnectionChangedEventArgs>? DeviceConnectionChanged;
    public event EventHandler<ConnectedDevice?>? SelectedDeviceChanged;

    /// <summary>
    /// Recent platform-tools disable mDNS unless this is set before the daemon starts.
    /// Without it, Android 9 <c>_adb._tcp</c> and Android 11 TLS wireless debug never appear.
    /// </summary>
    public static void ApplyMdnsEnvironment()
    {
        Environment.SetEnvironmentVariable("ADB_MDNS_OPENSCREEN", "1");
        Environment.SetEnvironmentVariable("ADB_MDNS", "1");
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ApplyMdnsEnvironment();
        if (_isRunning) return;

        var adbPath = ResolveAdbPath();
        _logger.Information("Starting ADB server using {AdbPath}", adbPath);

        if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath))
        {
            throw new FileNotFoundException(
                "adb.exe bulunamadı. tools/adb klasörüne platform-tools koyun veya ADB_PATH ayarlayın.",
                adbPath);
        }

        var server = AdbServer.Instance;
        var status = await server.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsRunning && !await MdnsDaemonLooksEnabledAsync(adbPath, cancellationToken).ConfigureAwait(false))
        {
            _logger.Information("ADB mDNS kapalı; sunucu ADB_MDNS_OPENSCREEN=1 ile yeniden başlatılıyor");
            await RunKillServerCliAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(300, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            status = await server.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!status.IsRunning)
        {
            var result = await server.StartServerAsync(adbPath, restartServerIfNewer: false, cancellationToken)
                .ConfigureAwait(false);
            _logger.Information("ADB StartServer result: {Result}", result);
        }

        _adbClient = new AdbClient();
        _monitorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // ADB server always listens on 5037 (not device WiFi port 5555)
        var adbEndpoint = new IPEndPoint(IPAddress.Loopback, 5037);
        await WaitForAdbServerAsync(adbEndpoint, cancellationToken).ConfigureAwait(false);

        _deviceMonitor = new DeviceMonitor(new AdbSocket(adbEndpoint));
        _deviceMonitor.DeviceConnected += OnDeviceConnected;
        _deviceMonitor.DeviceDisconnected += OnDeviceDisconnected;
        await _deviceMonitor.StartAsync(_monitorCts.Token).ConfigureAwait(false);

        await RefreshDevicesAsync(cancellationToken).ConfigureAwait(false);
        _isRunning = true;
    }

    private static async Task WaitForAdbServerAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(endpoint.Address, endpoint.Port, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch
            {
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _isRunning = false;
        if (_deviceMonitor is not null)
        {
            _deviceMonitor.DeviceConnected -= OnDeviceConnected;
            _deviceMonitor.DeviceDisconnected -= OnDeviceDisconnected;
            try
            {
                await _deviceMonitor.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "DeviceMonitor dispose failed during stop");
            }

            _deviceMonitor = null;
        }

        try
        {
            _monitorCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        _monitorCts?.Dispose();
        _monitorCts = null;
        _adbClient = null;

        // Host ADB daemon (port 5037) otherwise stays running after the UI closes.
        await KillAdbServerAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> MdnsDaemonLooksEnabledAsync(string adbPath, CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = "mdns check",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.Environment["ADB_MDNS_OPENSCREEN"] = "1";
            psi.Environment["ADB_MDNS"] = "1";

            using var process = Process.Start(psi);
            if (process is null)
                return true;

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var text = $"{stdout}\n{stderr}";
            _logger.Information("adb mdns check: {Output}", text.Trim());

            if (text.Contains("unknown command", StringComparison.OrdinalIgnoreCase)
                || text.Contains("unrecognized", StringComparison.OrdinalIgnoreCase))
                return true;

            if (text.Contains("disabled", StringComparison.OrdinalIgnoreCase)
                || text.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
                || text.Contains("not enabled", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(text))
                return false;

            return text.Contains("version", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("openscreen", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("bonjour", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("mdns", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "adb mdns check failed");
            return true;
        }
    }

    private async Task KillAdbServerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await AdbServer.Instance.StopServerAsync(cancellationToken).ConfigureAwait(false);
            _logger.Information("ADB server stopped via AdbServer.StopServerAsync");
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "AdbServer.StopServerAsync failed; falling back to kill-server");
            await RunKillServerCliAsync(cancellationToken).ConfigureAwait(false);
        }

        // Temporary adb.exe forks / stubborn host process.
        TryKillManagedAdbProcesses();
    }

    private async Task RunKillServerCliAsync(CancellationToken cancellationToken)
    {
        try
        {
            var adbPath = ResolveAdbPath();
            if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath))
                return;

            var psi = new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = "kill-server",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process is null)
                return;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKillProcessTree(process);
            }

            _logger.Information("adb kill-server completed (exit {Code})", process.HasExited ? process.ExitCode : -1);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "adb kill-server CLI failed");
        }
    }

    /// <summary>
    /// Kills leftover <c>adb.exe</c> started from this app's directory or the resolved ADB path.
    /// Does not touch unrelated ADB copies (e.g. a different SDK path used by another tool).
    /// </summary>
    private void TryKillManagedAdbProcesses()
    {
        var resolved = ResolveAdbPath();
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;

        foreach (var process in Process.GetProcessesByName("adb"))
        {
            try
            {
                using (process)
                {
                    string? path = null;
                    try
                    {
                        path = process.MainModule?.FileName;
                    }
                    catch
                    {
                        // Access denied — skip rather than kill unknown process.
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(path))
                        continue;

                    var isOurs =
                        path.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)
                        || (!string.IsNullOrWhiteSpace(resolved)
                            && string.Equals(path, resolved, StringComparison.OrdinalIgnoreCase));

                    if (!isOurs || process.HasExited)
                        continue;

                    process.Kill(entireProcessTree: true);
                    _logger.Information("Killed leftover adb process {Pid} ({Path})", process.Id, path);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not kill adb process");
            }
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // ignore
        }
    }

    public async Task<IReadOnlyList<ConnectedDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        EnsureClient();
        return await RefreshDevicesAsync(cancellationToken).ConfigureAwait(false);
    }

    public void SelectDevice(ConnectedDevice? device)
    {
        lock (_sync)
        {
            _selectedDevice = device;
        }

        SelectedDeviceChanged?.Invoke(this, device);
    }

    public Task<string> ExecuteShellAsync(string command, CancellationToken cancellationToken = default) =>
        RunOnDeviceChannelAsync(
            () => ExecuteShellUngatedAsync(command, ProcessWaitHelper.DefaultShellTimeout, cancellationToken),
            cancellationToken);

    public Task RunExclusiveAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default) =>
        RunOnDeviceChannelAsync(() => action(cancellationToken), cancellationToken);

    /// <summary>DeviceChannel zaten tutuluyorsa (ör. AdbSyncService) kullanın.</summary>
    internal async Task<string> ExecuteShellUngatedAsync(
        string command,
        CancellationToken cancellationToken = default) =>
        await ExecuteShellUngatedAsync(command, ProcessWaitHelper.DefaultShellTimeout, cancellationToken)
            .ConfigureAwait(false);

    internal async Task<string> ExecuteShellUngatedAsync(
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        EnsureClient();
        var device = GetSelectedDeviceData();
        var receiver = new ConsoleOutputReceiver();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await _adbClient!.ExecuteRemoteCommandAsync(
                    command,
                    device,
                    receiver,
                    Encoding.UTF8,
                    timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"adb shell zaman aşımı ({timeout.TotalSeconds:0}s): {command}");
        }

        return receiver.ToString() ?? string.Empty;
    }

    internal async Task<T> RunOnDeviceChannelAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        await DeviceTransportGate.WaitAcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            DeviceChannel.Release();
        }
    }

    internal async Task RunOnDeviceChannelAsync(
        Func<Task> action,
        CancellationToken cancellationToken = default)
    {
        await DeviceTransportGate.WaitAcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            DeviceChannel.Release();
        }
    }

    public async Task<bool> ConnectWifiAsync(string ipAddress, int port = 5555, CancellationToken cancellationToken = default)
    {
        EnsureClient();
        try
        {
            var result = await _adbClient!.ConnectAsync(ipAddress, port, cancellationToken).ConfigureAwait(false);
            _logger.Information("WiFi connect result: {Result}", result);
            await RefreshDevicesAsync(cancellationToken).ConfigureAwait(false);
            return AdbConnectResult.IsSuccess(result);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "WiFi connect failed for {Ip}:{Port}", ipAddress, port);
            return false;
        }
    }

    public async Task<bool> PairWifiAsync(
        string ipAddress,
        int port,
        string pairingCode,
        CancellationToken cancellationToken = default)
    {
        EnsureClient();
        if (string.IsNullOrWhiteSpace(pairingCode))
            throw new ArgumentException("Eşleştirme kodu gerekli.", nameof(pairingCode));

        try
        {
            var result = await _adbClient!.PairAsync(ipAddress, port, pairingCode.Trim(), cancellationToken)
                .ConfigureAwait(false);
            _logger.Information("WiFi pair result for {Ip}:{Port}: {Result}", ipAddress, port, result);
            return AdbConnectResult.IsSuccess(result?.ToString())
                   || (result?.ToString() ?? string.Empty)
                       .Contains("Successfully", StringComparison.OrdinalIgnoreCase)
                   || (result?.ToString() ?? string.Empty)
                       .Contains("paired", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "WiFi pair failed for {Ip}:{Port}", ipAddress, port);
            return false;
        }
    }

    public async Task<InstallResult> InstallLocalPackagesAsync(
        IReadOnlyList<string> localApkPaths,
        CancellationToken cancellationToken = default)
    {
        EnsureClient();
        if (localApkPaths.Count == 0)
            return new InstallResult { Success = false, Message = "Yüklenecek APK yok." };

        foreach (var path in localApkPaths)
        {
            if (!File.Exists(path))
                return new InstallResult { Success = false, Message = $"Dosya bulunamadı: {path}" };
        }

        var device = SelectedDevice
                     ?? throw new InvalidOperationException("Cihaz bağlı değil veya seçilmedi.");

        var adbPath = ResolveAdbPath();
        if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath))
            throw new FileNotFoundException("adb.exe bulunamadı.", adbPath);

        var quoted = string.Join(" ", localApkPaths.Select(p => $"\"{p}\""));
        var args = localApkPaths.Count == 1
            ? $"-s \"{device.Serial}\" install -r {quoted}"
            : $"-s \"{device.Serial}\" install-multiple -r {quoted}";

        _logger.Information("Installing {Count} package(s) via adb on {Serial}", localApkPaths.Count, device.Serial);

        return await RunOnDeviceChannelAsync(async () =>
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi)
                                ?? throw new InvalidOperationException("adb install başlatılamadı.");
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            if (!string.IsNullOrWhiteSpace(stderr) && !string.IsNullOrWhiteSpace(stdout))
                output = $"{stdout.Trim()}\n{stderr.Trim()}";

            var success = process.ExitCode == 0
                          && output.Contains("Success", StringComparison.OrdinalIgnoreCase);

            return new InstallResult
            {
                Success = success,
                Message = string.IsNullOrWhiteSpace(output)
                    ? (success ? "Success" : $"adb install exit {process.ExitCode}")
                    : output.Trim()
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceToolResult> SideloadAsync(
        string zipPath,
        IProgress<SideloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            return new DeviceToolResult { Success = false, Message = "ROM zip dosyası bulunamadı." };

        var device = SelectedDevice
                     ?? throw new InvalidOperationException("Cihaz bağlı değil veya seçilmedi.");

        var adbPath = ResolveAdbPath();
        if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath))
            throw new FileNotFoundException("adb.exe bulunamadı.", adbPath);

        await WaitForAdbDeviceCliAsync(adbPath, device.Serial, TimeSpan.FromSeconds(45), cancellationToken)
            .ConfigureAwait(false);

        return await RunOnDeviceChannelAsync(async () =>
        {
            _logger.Information("Starting adb sideload of {Zip} on {Serial}", zipPath, device.Serial);
            progress?.Report(new SideloadProgress { Percent = 0, Line = "Sideload başlıyor…" });

            var psi = AdbProcessFactory.Create(adbPath, $"-s \"{device.Serial}\" sideload \"{zipPath}\"");
            using var process = Process.Start(psi)
                                ?? throw new InvalidOperationException("adb sideload başlatılamadı.");

            var lastPercent = 0;
            var sawSuccess = false;
            var log = new StringBuilder();

            void HandleLine(string? line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    return;

                log.AppendLine(line);
                if (SideloadProgressParser.IsSuccessMarker(line))
                    sawSuccess = true;

                var parsed = SideloadProgressParser.TryParsePercent(line);
                if (parsed is > 0)
                    lastPercent = Math.Max(lastPercent, parsed.Value);

                progress?.Report(new SideloadProgress
                {
                    Percent = lastPercent,
                    Line = line.Trim()
                });
            }

            await using var killReg = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "adb sideload kill failed");
                }
            });

            var stdoutTask = ReadProcessLinesAsync(process.StandardOutput, HandleLine, cancellationToken);
            var stderrTask = ReadProcessLinesAsync(process.StandardError, HandleLine, cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

            var output = log.ToString().Trim();
            var success = sawSuccess
                          || process.ExitCode == 0
                          || lastPercent >= 100;

            if (!success && SideloadProgressParser.IsTransientDeviceError(output))
            {
                return new DeviceToolResult
                {
                    Success = false,
                    Message = "Sideload cihazı görünmedi. TWRP'de ADB Sideload'ı açıp tekrar deneyin."
                };
            }

            if (success)
                progress?.Report(new SideloadProgress { Percent = 100, Line = "Sideload tamamlandı." });

            return new DeviceToolResult
            {
                Success = success,
                Message = string.IsNullOrWhiteSpace(output)
                    ? (success ? "Sideload tamamlandı." : $"adb sideload exit {process.ExitCode}")
                    : output
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceToolResult> RebootTransportAsync(
        string? target,
        string? serial = null,
        CancellationToken cancellationToken = default)
    {
        serial ??= SelectedDevice?.Serial;
        if (string.IsNullOrWhiteSpace(serial))
        {
            var devices = await GetDevicesAsync(cancellationToken).ConfigureAwait(false);
            serial = devices.FirstOrDefault(d => d.IsAdbReady)?.Serial;
        }

        if (string.IsNullOrWhiteSpace(serial))
            return new DeviceToolResult { Success = false, Message = "Reboot için ADB cihazı yok." };

        var adbPath = ResolveAdbPath();
        if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath))
            return new DeviceToolResult { Success = false, Message = "adb.exe bulunamadı." };

        var command = string.IsNullOrWhiteSpace(target) ? "reboot" : $"reboot {target.Trim()}";
        _logger.Information("adb transport: {Command} on {Serial}", command, serial);

        return await RunOnDeviceChannelAsync(async () =>
        {
            var (exitCode, output) = await AdbProcessFactory
                .RunOnceAsync(adbPath, $"-s \"{serial}\" {command}", cancellationToken)
                .ConfigureAwait(false);

            var ok = exitCode == 0
                     && !output.Contains("error", StringComparison.OrdinalIgnoreCase)
                     && !output.Contains("failed", StringComparison.OrdinalIgnoreCase);

            return new DeviceToolResult
            {
                Success = ok,
                Message = string.IsNullOrWhiteSpace(output)
                    ? (ok ? $"{command} gönderildi." : $"adb {command} exit {exitCode}")
                    : output.Trim()
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<(int ExitCode, string Output)> RunHostAdbAsync(
        string? serial,
        string arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        RunOnDeviceChannelAsync(async () =>
        {
            var adbPath = ResolveAdbPath();
            if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath))
                return (-1, "adb.exe bulunamadı.");

            var fullArgs = string.IsNullOrWhiteSpace(serial)
                ? arguments.Trim()
                : $"-s \"{serial}\" {arguments.Trim()}";

            var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(2);
            return await AdbProcessFactory
                .RunOnceWithTimeoutAsync(adbPath, fullArgs, effectiveTimeout, cancellationToken)
                .ConfigureAwait(false);
        }, cancellationToken);

    public async Task<AdbAppBackupResult> RunInteractiveAppBackupAsync(
        string packageName,
        string outputAbPath,
        IProgress<AdbAppBackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureClient();
        var device = SelectedDevice
                     ?? throw new InvalidOperationException("Cihaz bağlı değil veya seçilmedi.");

        await TryWakeDeviceAsync(cancellationToken).ConfigureAwait(false);

        return await RunOnDeviceChannelAsync(
            () => RunInteractiveAppBackupCoreAsync(device.Serial, packageName, outputAbPath, progress, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AdbAppBackupResult> RunInteractiveAppBackupCoreAsync(
        string serial,
        string packageName,
        string outputAbPath,
        IProgress<AdbAppBackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var adbPath = ResolveAdbPath();
        if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath))
            throw new FileNotFoundException("adb.exe bulunamadı.", adbPath);

        outputAbPath = Path.GetFullPath(outputAbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputAbPath)!);

        const int maxAttempts = 3;
        var perAttemptTimeout = TimeSpan.FromMinutes(12);
        string lastOutput = string.Empty;
        var lastExitCode = -1;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReportBackupProgress(progress, attempt, maxAttempts, 0, 0,
                "Telefonda yedek onayı bekleniyor — 'Verilerimi yedekle' / 'Back up my data' butonuna basın. Şifre KULLANMAYIN.",
                waitingForUser: true);

            if (File.Exists(outputAbPath))
                File.Delete(outputAbPath);

            var attemptResult = await RunSingleAppBackupAttemptAsync(
                    adbPath,
                    serial,
                    packageName,
                    outputAbPath,
                    perAttemptTimeout,
                    attempt,
                    maxAttempts,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            lastOutput = attemptResult.Output;
            lastExitCode = attemptResult.ExitCode;

            if (attemptResult.Success)
            {
                return new AdbAppBackupResult
                {
                    Success = true,
                    OutputAbPath = outputAbPath,
                    Message = "ADB yedekleme tamamlandı.",
                    ExitCode = attemptResult.ExitCode,
                    AttemptsUsed = attempt
                };
            }

            if (!attemptResult.ExitedTooFast || attempt >= maxAttempts)
                break;

            ReportBackupProgress(progress, attempt, maxAttempts, 0, 0,
                "Onay penceresi kaçırılmış olabilir — 5 saniye sonra tekrar denenecek…",
                waitingForUser: false);
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }

        return new AdbAppBackupResult
        {
            Success = false,
            Message = string.IsNullOrWhiteSpace(lastOutput)
                ? "ADB yedekleme başarısız — telefonda onay verildi mi? Şifresiz yedekleyin veya root/downgrade deneyin."
                : lastOutput,
            ExitCode = lastExitCode,
            AttemptsUsed = maxAttempts
        };
    }

    private async Task TryWakeDeviceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteShellAsync("input keyevent KEYCODE_WAKEUP", cancellationToken).ConfigureAwait(false);
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
            await ExecuteShellAsync("input keyevent KEYCODE_MENU", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Wake device before adb backup failed");
        }
    }

    private sealed record SingleBackupAttemptResult(
        bool Success,
        bool ExitedTooFast,
        int ExitCode,
        string Output);

    private async Task<SingleBackupAttemptResult> RunSingleAppBackupAttemptAsync(
        string adbPath,
        string serial,
        string packageName,
        string outputAbPath,
        TimeSpan timeout,
        int attempt,
        int maxAttempts,
        IProgress<AdbAppBackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var args = $"-s \"{serial}\" backup -f \"{outputAbPath}\" -noapk {packageName}";
        var psi = AdbProcessFactory.Create(adbPath, args);
        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("adb backup başlatılamadı.");

        var log = new StringBuilder();
        void HandleLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;
            log.AppendLine(line);
        }

        var stdoutTask = ReadProcessLinesAsync(process.StandardOutput, HandleLine, cancellationToken);
        var stderrTask = ReadProcessLinesAsync(process.StandardError, HandleLine, cancellationToken);

        var started = Environment.TickCount64;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        long lastSize = 0;
        try
        {
            while (!process.HasExited)
            {
                timeoutCts.Token.ThrowIfCancellationRequested();

                var elapsedSec = (int)((Environment.TickCount64 - started) / 1000);
                var fileSizeKb = 0;
                if (File.Exists(outputAbPath))
                {
                    var size = new FileInfo(outputAbPath).Length;
                    fileSizeKb = (int)Math.Min(int.MaxValue, size / 1024);
                    if (size > lastSize)
                    {
                        lastSize = size;
                        ReportBackupProgress(progress, attempt, maxAttempts, elapsedSec, fileSizeKb,
                            $"Yedek alınıyor… {fileSizeKb} KB",
                            waitingForUser: false);
                    }
                    else
                    {
                        ReportBackupProgress(progress, attempt, maxAttempts, elapsedSec, fileSizeKb,
                            $"Telefonda onay bekleniyor… ({elapsedSec} sn)",
                            waitingForUser: true);
                    }
                }
                else
                {
                    ReportBackupProgress(progress, attempt, maxAttempts, elapsedSec, 0,
                        $"Telefonda yedek onay penceresini açın… ({elapsedSec} sn)",
                        waitingForUser: true);
                }

                await Task.Delay(500, timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "adb backup kill on timeout failed");
            }

            return new SingleBackupAttemptResult(false, false, -1, "ADB yedekleme zaman aşımı — telefonda onay verin.");
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        await Task.Delay(800, cancellationToken).ConfigureAwait(false);

        var elapsedMs = Environment.TickCount64 - started;
        var exitedTooFast = elapsedMs < 4000 && !IsValidAbBackupFile(outputAbPath);
        var output = log.ToString().Trim();
        var success = IsValidAbBackupFile(outputAbPath);

        _logger.Information(
            "adb backup attempt {Attempt}: exit={Exit} elapsedMs={Elapsed} tooFast={TooFast} validAb={Valid} output={Output}",
            attempt, process.ExitCode, elapsedMs, exitedTooFast, success, output);

        return new SingleBackupAttemptResult(success, exitedTooFast, process.ExitCode, output);
    }

    private static bool IsValidAbBackupFile(string path)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            var info = new FileInfo(path);
            if (info.Length < 512)
                return false;

            using var fs = File.OpenRead(path);
            using var reader = new StreamReader(fs);
            return reader.ReadLine() == "ANDROID BACKUP";
        }
        catch
        {
            return false;
        }
    }

    private static void ReportBackupProgress(
        IProgress<AdbAppBackupProgress>? progress,
        int attempt,
        int maxAttempts,
        int elapsedSeconds,
        int fileSizeKb,
        string message,
        bool waitingForUser) =>
        progress?.Report(new AdbAppBackupProgress
        {
            Attempt = attempt,
            MaxAttempts = maxAttempts,
            ElapsedSeconds = elapsedSeconds,
            FileSizeKb = fileSizeKb,
            Message = message,
            WaitingForUserConfirmation = waitingForUser
        });

    public async Task<BlockDumpResult> ExecOutToFileAsync(
        string remoteCommand,
        string localFilePath,
        long expectedBytes = 0,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(remoteCommand))
            return new BlockDumpResult { Success = false, Message = "Uzak komut boş." };

        var device = SelectedDevice
                     ?? throw new InvalidOperationException("Cihaz bağlı değil veya seçilmedi.");

        var adbPath = ResolveAdbPath();
        if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath))
            throw new FileNotFoundException("adb.exe bulunamadı.", adbPath);

        var directory = Path.GetDirectoryName(localFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await DeviceTransportGate.WaitAcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ExecOutToFileCoreAsync(
                    device.Serial,
                    adbPath,
                    remoteCommand,
                    localFilePath,
                    expectedBytes,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            DeviceChannel.Release();
        }
    }

    private async Task<BlockDumpResult> ExecOutToFileCoreAsync(
        string serial,
        string adbPath,
        string remoteCommand,
        string localFilePath,
        long expectedBytes,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var escaped = remoteCommand.Replace("\"", "\\\"", StringComparison.Ordinal);
        var args = $"-s \"{serial}\" exec-out \"{escaped}\"";
        _logger.Information("exec-out dump {Command} -> {Path}", remoteCommand, localFilePath);

        var psi = AdbProcessFactory.CreateBinary(adbPath, args);
        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("adb exec-out başlatılamadı.");

        using var dumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        dumpCts.CancelAfter(TimeSpan.FromMinutes(30));
        var ct = dumpCts.Token;

        await using var killReg = ct.Register(() =>
        {
            ProcessWaitHelper.TryKill(process);
        });

        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        long written = 0;
        long lastReport = 0;
        string sha = "";

        try
        {
            await using var fs = new FileStream(
                localFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            try
            {
                var stdout = process.StandardOutput.BaseStream;
                int read;
                while ((read = await stdout.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)
                           .ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    hasher.AppendData(buffer.AsSpan(0, read));
                    written += read;

                    if (expectedBytes <= 0 || written - lastReport < 8 * 1024 * 1024)
                        continue;

                    lastReport = written;
                    progress?.Report(new TransferProgress
                    {
                        FileName = Path.GetFileName(localFilePath),
                        BytesTransferred = written,
                        TotalBytes = expectedBytes
                    });
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            sha = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryDelete(localFilePath);
            return new BlockDumpResult { Success = false, Message = ex.Message, LocalPath = localFilePath };
        }

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var stderr = "";
        try
        {
            stderr = (await stderrTask.ConfigureAwait(false)).Trim();
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "exec-out stderr read failed");
        }

        progress?.Report(new TransferProgress
        {
            FileName = Path.GetFileName(localFilePath),
            BytesTransferred = written,
            TotalBytes = expectedBytes > 0 ? expectedBytes : written
        });

        if (written <= 0)
        {
            TryDelete(localFilePath);
            return new BlockDumpResult
            {
                Success = false,
                Message = string.IsNullOrWhiteSpace(stderr)
                    ? "Döküm boş geldi. Recovery veya root yetkisi gerekir."
                    : stderr
            };
        }

        if (expectedBytes > 0 && written < expectedBytes / 2)
        {
            TryDelete(localFilePath);
            return new BlockDumpResult
            {
                Success = false,
                Message = $"Eksik döküm: {written} / {expectedBytes} bayt. {stderr}".Trim()
            };
        }

        return new BlockDumpResult
        {
            Success = true,
            Message = written == expectedBytes || expectedBytes <= 0
                ? $"{written} bayt yazıldı"
                : $"{written} / {expectedBytes} bayt yazıldı",
            BytesWritten = written,
            Sha256 = sha,
            LocalPath = localFilePath
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // leftover partial dump
        }
    }

    private static async Task WaitForAdbDeviceCliAsync(
        string adbPath,
        string serial,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await AdbProcessFactory.RunOnceWithTimeoutAsync(
                    adbPath,
                    $"-s \"{serial}\" wait-for-device",
                    timeout,
                    timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout: sideload denemesi yine de yapılsın; TWRP bazen geç bildirir.
        }
    }

    private static async Task ReadProcessLinesAsync(
        StreamReader reader,
        Action<string?> onLine,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            onLine(line);
    }

    public async Task EnableTcpipAsync(int port = 5555, CancellationToken cancellationToken = default)
    {
        EnsureClient();
        var device = SelectedDevice
                     ?? throw new InvalidOperationException("Cihaz bağlı değil veya seçilmedi.");

        var adbPath = ResolveAdbPath();
        if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath))
            throw new FileNotFoundException("adb.exe bulunamadı.", adbPath);

        _logger.Information("Enabling ADB tcpip on {Serial} port {Port}", device.Serial, port);

        await RunOnDeviceChannelAsync(async () =>
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = $"-s \"{device.Serial}\" tcpip {port}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi)
                                ?? throw new InvalidOperationException("adb tcpip başlatılamadı.");
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                throw new InvalidOperationException(
                    $"adb tcpip başarısız (exit {process.ExitCode}): {detail.Trim()}");
            }

            _logger.Information("adb tcpip ok: {Output}", stdout.Trim());
        }, cancellationToken).ConfigureAwait(false);

        await Task.Delay(800, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetDeviceIpAsync(CancellationToken cancellationToken = default)
    {
        EnsureClient();

        var commands =
            new[]
            {
                "ip -f inet addr show wlan0",
                "ip -f inet addr show wlan1",
                "ip route get 8.8.8.8",
                "getprop dhcp.wlan0.ipaddress",
                "ifconfig wlan0"
            };

        foreach (var command in commands)
        {
            try
            {
                var output = await ExecuteShellAsync(command, cancellationToken).ConfigureAwait(false);
                var ip = DeviceOutputParsers.ParseIpv4Address(output);
                if (!string.IsNullOrWhiteSpace(ip) && !ip.StartsWith("127.", StringComparison.Ordinal))
                    return ip;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "IP probe failed for {Command}", command);
            }
        }

        return null;
    }

    public async Task<UsbWifiSwitchResult> SwitchUsbToWifiAsync(
        int port = 5555,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (SelectedDevice is null)
            {
                return new UsbWifiSwitchResult
                {
                    Success = false,
                    Message = "Önce USB ile bir cihaz bağlayın ve seçin."
                };
            }

            var ip = await GetDeviceIpAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(ip))
            {
                return new UsbWifiSwitchResult
                {
                    Success = false,
                    Message = "Cihazın Wi‑Fi IP adresi alınamadı. Telefonun Wi‑Fi’ye bağlı olduğundan emin olun."
                };
            }

            await EnableTcpipAsync(port, cancellationToken).ConfigureAwait(false);
            var connected = await ConnectWifiAsync(ip, port, cancellationToken).ConfigureAwait(false);
            return new UsbWifiSwitchResult
            {
                Success = connected,
                IpAddress = ip,
                Port = port,
                Message = connected
                    ? $"Kablosuz ADB bağlandı: {ip}:{port}"
                    : $"tcpip açıldı ama {ip}:{port} bağlantısı kurulamadı."
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "USB→WiFi switch failed");
            return new UsbWifiSwitchResult
            {
                Success = false,
                Message = ex.Message
            };
        }
    }

    public async Task<ConnectedDevice?> WaitForAdbDeviceAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var devices = await GetDevicesAsync(cancellationToken).ConfigureAwait(false);
                var found = devices.FirstOrDefault(d => d.IsAdbReady);
                if (found is not null)
                {
                    if (SelectedDevice is null
                        || !string.Equals(SelectedDevice.Serial, found.Serial, StringComparison.OrdinalIgnoreCase))
                    {
                        SelectDevice(found);
                    }

                    return found;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "WaitForAdbDevice poll failed");
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    internal DeviceData GetSelectedDeviceData()
    {
        var selected = SelectedDevice
                       ?? throw new InvalidOperationException("Cihaz bağlı değil veya seçilmedi.");

        EnsureClient();
        var devices = _adbClient!.GetDevices().ToList();
        var match = devices.FirstOrDefault(d =>
            string.Equals(d.Serial, selected.Serial, StringComparison.OrdinalIgnoreCase));

        if (match is null || match.IsEmpty || string.IsNullOrWhiteSpace(match.Serial))
            throw new InvalidOperationException($"Cihaz bulunamadı: {selected.Serial}");

        return match;
    }

    private async Task<IReadOnlyList<ConnectedDevice>> RefreshDevicesAsync(CancellationToken cancellationToken)
    {
        EnsureClient();
        var raw = (await _adbClient!.GetDevicesAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var mapped = raw.Select(MapDevice).ToList();

        string? previousSerial;
        string? newSerial;
        lock (_sync)
        {
            previousSerial = _selectedDevice?.Serial;
            _devices.Clear();
            _devices.AddRange(mapped);

            if (_selectedDevice is not null)
            {
                var updated = _devices.FirstOrDefault(d =>
                    string.Equals(d.Serial, _selectedDevice.Serial, StringComparison.OrdinalIgnoreCase));
                if (updated is not null)
                    _selectedDevice = updated;
                else
                    _selectedDevice = _devices.FirstOrDefault(d => d.IsAdbReady) ?? _devices.FirstOrDefault();
            }
            else
            {
                _selectedDevice = _devices.FirstOrDefault(d => d.IsAdbReady) ?? _devices.FirstOrDefault();
            }

            newSerial = _selectedDevice?.Serial;
        }

        // Watchdog / GetDevicesAsync polls often — do not spam selection handlers (Gallery refresh storm).
        if (!string.Equals(previousSerial, newSerial, StringComparison.OrdinalIgnoreCase))
            SelectedDeviceChanged?.Invoke(this, SelectedDevice);

        return mapped;
    }

    private void OnDeviceConnected(object? sender, DeviceDataConnectEventArgs e)
    {
        var mapped = MapDevice(e.Device);
        string? previousSerial;
        string? newSerial;
        lock (_sync)
        {
            previousSerial = _selectedDevice?.Serial;
            if (_devices.All(d => d.Serial != mapped.Serial))
                _devices.Add(mapped);

            _selectedDevice ??= mapped;
            newSerial = _selectedDevice?.Serial;
        }

        DeviceConnectionChanged?.Invoke(this, new DeviceConnectionChangedEventArgs
        {
            Device = mapped,
            IsConnected = true
        });

        if (!string.Equals(previousSerial, newSerial, StringComparison.OrdinalIgnoreCase))
            SelectedDeviceChanged?.Invoke(this, SelectedDevice);
    }

    private void OnDeviceDisconnected(object? sender, DeviceDataConnectEventArgs e)
    {
        var mapped = MapDevice(e.Device);
        string? previousSerial;
        string? newSerial;
        lock (_sync)
        {
            previousSerial = _selectedDevice?.Serial;
            _devices.RemoveAll(d => d.Serial == mapped.Serial);
            if (_selectedDevice?.Serial == mapped.Serial)
                _selectedDevice = _devices.FirstOrDefault();
            newSerial = _selectedDevice?.Serial;
        }

        DeviceConnectionChanged?.Invoke(this, new DeviceConnectionChangedEventArgs
        {
            Device = mapped,
            IsConnected = false
        });

        if (!string.Equals(previousSerial, newSerial, StringComparison.OrdinalIgnoreCase))
            SelectedDeviceChanged?.Invoke(this, SelectedDevice);
    }

    private void EnsureClient()
    {
        if (_adbClient is null)
            throw new InvalidOperationException("ADB servisi başlatılmadı. StartAsync çağırın.");
    }

    private static ConnectedDevice MapDevice(DeviceData device) => new()
    {
        Serial = device.Serial ?? string.Empty,
        Model = device.Model ?? string.Empty,
        Product = device.Product ?? string.Empty,
        State = device.State.ToString()
    };

    internal static string ResolveAdbPath()
    {
        var env = Environment.GetEnvironmentVariable("ADB_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;

        var bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "adb", "adb.exe");
        if (File.Exists(bundled))
            return bundled;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var sdkCandidate = Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe");
        if (File.Exists(sdkCandidate))
            return sdkCandidate;

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), "adb.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        return bundled;
    }
}
