using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using AndroidManager.Core;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Core.Services;
using Serilog;

namespace AndroidManager.Device.Services;

public sealed class LogcatService : ILogcatService
{
    private static readonly Regex ThreadTimeRegex = new(
        @"^(?<ts>\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d+)\s+(?<pid>\d+)\s+(?<tid>\d+)\s+(?<level>[VDIWEF])\s+(?<tag>[^:]+):\s?(?<msg>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IAdbService _adb;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private CancellationTokenSource? _readCts;
    private LogcatStartOptions _options = new();
    private bool _disposed;

    public bool IsRunning => _process is { HasExited: false };

    public event EventHandler<LogcatLine>? LineReceived;
    public event EventHandler? Stopped;

    public LogcatService(IAdbService adb, ILogger? logger = null)
    {
        _adb = adb;
        _logger = logger ?? Log.ForContext<LogcatService>();
    }

    public async Task StartAsync(LogcatStartOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
                await StopCoreAsync().ConfigureAwait(false);

            var device = _adb.SelectedDevice
                         ?? throw new InvalidOperationException("Cihaz bağlı değil veya seçilmedi.");
            var adbPath = AdbService.ResolveAdbPath();
            if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath))
                throw new FileNotFoundException("adb.exe bulunamadı.", adbPath);

            if (options.ClearBeforeStart)
                await ClearDeviceLogAsync(cancellationToken).ConfigureAwait(false);

            var args = BuildArgs(device.Serial, options);
            var psi = AdbProcessFactory.Create(adbPath, args);
            var process = Process.Start(psi)
                          ?? throw new InvalidOperationException("logcat süreci başlatılamadı.");

            _process = process;
            _readCts = new CancellationTokenSource();
            ObservedTask.Run(PumpLinesAsync(process.StandardOutput, _readCts.Token));
            ObservedTask.Run(PumpLinesAsync(process.StandardError, _readCts.Token));
            ObservedTask.Run(WatchExitAsync(process, _readCts.Token));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
        {
            _logger.Warning("Logcat StopAsync gate timeout");
            return;
        }
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearDeviceLogAsync(CancellationToken cancellationToken = default)
    {
        var device = _adb.SelectedDevice
                     ?? throw new InvalidOperationException("Cihaz bağlı değil veya seçilmedi.");
        var adbPath = AdbService.ResolveAdbPath();
        if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath))
            throw new FileNotFoundException("adb.exe bulunamadı.", adbPath);

        await AdbProcessFactory.RunOnceAsync(
            adbPath,
            $"-s \"{device.Serial}\" logcat -c",
            cancellationToken).ConfigureAwait(false);
    }

    private static string BuildArgs(string serial, LogcatStartOptions options)
    {
        var buffer = string.IsNullOrWhiteSpace(options.Buffer) ? "main" : options.Buffer.Trim();
        var level = options.MinLevel switch
        {
            LogcatLevel.Debug => "D",
            LogcatLevel.Info => "I",
            LogcatLevel.Warn => "W",
            LogcatLevel.Error => "E",
            LogcatLevel.Fatal => "F",
            LogcatLevel.Silent => "S",
            _ => "V"
        };

        // Priority filter: either tag:level or *:level
        var filter = string.IsNullOrWhiteSpace(options.TagFilter)
            ? $"*:{level}"
            : $"{options.TagFilter.Trim()}:{level}";

        var sb = new System.Text.StringBuilder();
        sb.Append("-s \"").Append(serial).Append("\" logcat -v threadtime");
        if (!buffer.Equals("all", StringComparison.OrdinalIgnoreCase))
            sb.Append(" -b ").Append(buffer);
        sb.Append(' ').Append(filter);
        return sb.ToString();
    }

    private async Task PumpLinesAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                    break;

                var parsed = ParseLine(line);
                if (!PassesClientFilter(parsed))
                    continue;

                LineReceived?.Invoke(this, parsed);
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Logcat pump ended");
        }
    }

    private bool PassesClientFilter(LogcatLine line)
    {
        if (_options.MinLevel > line.Level && line.Level != LogcatLevel.Silent)
            return false;

        if (!string.IsNullOrWhiteSpace(_options.TextFilter) &&
            line.Raw.IndexOf(_options.TextFilter, StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        return true;
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
            _logger.Debug(ex, "Logcat exit watch ended");
        }

        Stopped?.Invoke(this, EventArgs.Empty);
    }

    private async Task StopCoreAsync()
    {
        try { _readCts?.Cancel(); } catch { /* ignore */ }
        _readCts?.Dispose();
        _readCts = null;

        try
        {
            if (_process is { HasExited: false })
            {
                await ProcessWaitHelper.WaitAfterKillAsync(_process).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Logcat process kill");
        }

        _process?.Dispose();
        _process = null;
    }

    internal static LogcatLine ParseLine(string raw)
    {
        var m = ThreadTimeRegex.Match(raw);
        if (!m.Success)
        {
            return new LogcatLine
            {
                Raw = raw,
                Message = raw,
                Level = LogcatLevel.Info
            };
        }

        return new LogcatLine
        {
            Raw = raw,
            Timestamp = m.Groups["ts"].Value,
            Pid = m.Groups["pid"].Value,
            Tid = m.Groups["tid"].Value,
            Level = ParseLevel(m.Groups["level"].Value),
            Tag = m.Groups["tag"].Value.Trim(),
            Message = m.Groups["msg"].Value
        };
    }

    private static LogcatLevel ParseLevel(string letter) => letter.ToUpperInvariant() switch
    {
        "V" => LogcatLevel.Verbose,
        "D" => LogcatLevel.Debug,
        "I" => LogcatLevel.Info,
        "W" => LogcatLevel.Warn,
        "E" => LogcatLevel.Error,
        "F" => LogcatLevel.Fatal,
        "S" => LogcatLevel.Silent,
        _ => LogcatLevel.Info
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
