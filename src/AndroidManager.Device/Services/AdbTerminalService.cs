using System.Diagnostics;
using System.IO;
using System.Text;
using AndroidManager.Core;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Services;
using Serilog;

namespace AndroidManager.Device.Services;

public sealed class AdbTerminalService : IAdbTerminalService
{
    private readonly IAdbService _adb;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private StreamWriter? _stdin;
    private CancellationTokenSource? _readCts;
    private bool _disposed;

    public bool IsSessionOpen => _process is { HasExited: false };

    public event EventHandler<string>? OutputReceived;
    public event EventHandler? SessionClosed;

    public AdbTerminalService(IAdbService adb, ILogger? logger = null)
    {
        _adb = adb;
        _logger = logger ?? Log.ForContext<AdbTerminalService>();
    }

    public async Task OpenSessionAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsSessionOpen)
                return;

            var device = _adb.SelectedDevice
                         ?? throw new InvalidOperationException("Cihaz bağlı değil veya seçilmedi.");
            var adbPath = AdbService.ResolveAdbPath();
            if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath))
                throw new FileNotFoundException("adb.exe bulunamadı.", adbPath);

            await CloseSessionCoreAsync().ConfigureAwait(false);

            var psi = AdbProcessFactory.Create(adbPath, $"-s \"{device.Serial}\" shell", redirectInput: true);
            var process = Process.Start(psi)
                          ?? throw new InvalidOperationException("ADB shell oturumu başlatılamadı.");

            _process = process;
            _stdin = process.StandardInput;
            _readCts = new CancellationTokenSource();
            ObservedTask.Run(PumpOutputAsync(process.StandardOutput, _readCts.Token));
            ObservedTask.Run(PumpOutputAsync(process.StandardError, _readCts.Token));
            ObservedTask.Run(WatchExitAsync(process, _readCts.Token));

            RaiseOutput($"# shell @ {device.Serial}\n");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseSessionAsync()
    {
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
        {
            _logger.Warning("Terminal CloseSessionAsync gate timeout");
            return;
        }
        try
        {
            await CloseSessionCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(line);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsSessionOpen || _stdin is null)
                throw new InvalidOperationException("Shell oturumu açık değil.");

            RaiseOutput($"> {line}\n");
            await _stdin.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> ExecuteOnceAsync(string command, CancellationToken cancellationToken = default)
    {
        var device = _adb.SelectedDevice
                     ?? throw new InvalidOperationException("Cihaz bağlı değil veya seçilmedi.");
        var adbPath = AdbService.ResolveAdbPath();
        if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath))
            throw new FileNotFoundException("adb.exe bulunamadı.", adbPath);

        // Prefer quoting via adb shell —c for one-shot without interactive session
        var escaped = command.Replace("\"", "\\\"", StringComparison.Ordinal);
        var (_, output) = await AdbProcessFactory.RunOnceWithTimeoutAsync(
            adbPath,
            $"-s \"{device.Serial}\" shell \"{escaped}\"",
            ProcessWaitHelper.DefaultShellTimeout,
            cancellationToken).ConfigureAwait(false);
        return output;
    }

    private async Task PumpOutputAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read <= 0)
                    break;

                RaiseOutput(new string(buffer, 0, read));
            }
        }
        catch (OperationCanceledException)
        {
            // expected on stop
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Terminal output pump ended");
        }
    }

    private async Task WatchExitAsync(Process process, CancellationToken ct)
    {
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Terminal exit watch ended");
        }

        RaiseOutput("\n# oturum kapandı\n");
        SessionClosed?.Invoke(this, EventArgs.Empty);
    }

    private async Task CloseSessionCoreAsync()
    {
        try { _readCts?.Cancel(); } catch { /* ignore */ }
        _readCts?.Dispose();
        _readCts = null;

        try
        {
            if (_stdin is not null)
            {
                await _stdin.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch { /* ignore */ }
        _stdin = null;

        try
        {
            if (_process is { HasExited: false })
            {
                await ProcessWaitHelper.WaitAfterKillAsync(_process).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Terminal process kill");
        }

        _process?.Dispose();
        _process = null;
    }

    private void RaiseOutput(string text)
    {
        if (!string.IsNullOrEmpty(text))
            OutputReceived?.Invoke(this, text);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await CloseSessionAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
