using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AndroidManager.Core;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Core.Services;
using Serilog;

namespace AndroidManager.Mirror.Services;

public sealed partial class MirrorService : IMirrorService
{
    private readonly IAdbService _adb;
    private readonly ISettingsService _settings;
    private readonly ILogger _logger;

    private Process? _scrcpyProcess;
    private CancellationTokenSource? _streamCts;
    private string _scrcpyPath = "";
    private string _adbPath = "";

    public bool IsStreaming { get; private set; }

    public bool IsScrcpyAvailable
    {
        get
        {
            RefreshToolPaths();
            return File.Exists(_scrcpyPath) && PlatformToolsPathResolver.IsScrcpyBundleComplete(_scrcpyPath);
        }
    }

    public event EventHandler<byte[]>? FrameReceived;
    public event EventHandler<MirrorErrorEventArgs>? ErrorOccurred;

    public MirrorService(IAdbService adb, ISettingsService settings, ILogger? logger = null)
    {
        _adb = adb;
        _settings = settings;
        _logger = logger ?? Log.ForContext<MirrorService>();
        RefreshToolPaths();
        _settings.SettingsChanged += (_, _) => RefreshToolPaths();
    }

    public async Task StartScrcpyAsync(ScrcpyOptions options, CancellationToken cancellationToken = default)
    {
        RefreshToolPaths();

        if (!File.Exists(_scrcpyPath))
            throw new FileNotFoundException(
                "scrcpy.exe bulunamadı. Ayarlardan yol seçin veya tools/scrcpy klasörüne kopyalayın.\n" +
                "İndir: https://github.com/Genymobile/scrcpy/releases",
                _scrcpyPath);

        if (!PlatformToolsPathResolver.IsScrcpyBundleComplete(_scrcpyPath))
            throw new FileNotFoundException(
                "scrcpy-server eksik. scrcpy.exe ile aynı klasörde tam scrcpy paketi olmalı.",
                _scrcpyPath);

        var serial = ResolveSerial(options.Serial);
        if (string.IsNullOrWhiteSpace(serial))
            throw new InvalidOperationException("Bağlı ADB cihazı yok. Sol panelden cihazı seçin.");

        await StopAsync().ConfigureAwait(false);

        var workDir = Path.GetDirectoryName(_scrcpyPath)
                      ?? AppDomain.CurrentDomain.BaseDirectory;

        var stderr = new StringBuilder();
        var startInfo = CreateScrcpyStartInfo(options, serial, _adbPath, workDir);
        _logger.Information(
            "scrcpy starting ({Serial}): {Exe} {Args}",
            serial,
            _scrcpyPath,
            string.Join(' ', startInfo.ArgumentList));

        _scrcpyProcess = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        _scrcpyProcess.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                stderr.AppendLine(e.Data);
                _logger.Debug("scrcpy: {Data}", e.Data);
            }
        };

        _scrcpyProcess.Exited += (_, _) =>
        {
            IsStreaming = false;
            var err = stderr.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(err))
                _logger.Warning("scrcpy exited: {Error}", err);
            else
                _logger.Information("scrcpy exited");
        };

        if (!_scrcpyProcess.Start())
            throw new InvalidOperationException("scrcpy başlatılamadı.");

        _scrcpyProcess.BeginErrorReadLine();
        IsStreaming = true;

        await Task.Delay(800, cancellationToken).ConfigureAwait(false);
        if (_scrcpyProcess.HasExited)
        {
            var err = stderr.ToString().Trim();
            IsStreaming = false;
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(err)
                    ? $"scrcpy hemen kapandı (exit {_scrcpyProcess.ExitCode})."
                    : $"scrcpy başarısız: {err}");
        }

        if (options.RecordPath is not null)
            await _scrcpyProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StartScreencapStreamAsync(int fps, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ResolveSerial(null)))
            throw new InvalidOperationException("Bağlı ADB cihazı yok. Sol panelden cihazı seçin.");

        await StopAsync().ConfigureAwait(false);

        _streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsStreaming = true;
        var token = _streamCts.Token;
        var delayMs = 1000 / Math.Max(1, fps);

        ObservedTask.Run(Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var frame = await CapturePngBytesAsync(token).ConfigureAwait(false);
                    if (frame is { Length: > 0 })
                        FrameReceived?.Invoke(this, frame);

                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Screencap frame error");
                    ErrorOccurred?.Invoke(this, new MirrorErrorEventArgs
                    {
                        Message = ex.Message,
                        Error = ex
                    });
                    try { await Task.Delay(1000, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }

            IsStreaming = false;
        }, token));

        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_streamCts is not null)
        {
            await _streamCts.CancelAsync();
            _streamCts.Dispose();
            _streamCts = null;
        }

        if (_scrcpyProcess is { HasExited: false })
        {
            try
            {
                await ProcessWaitHelper.WaitAfterKillAsync(_scrcpyProcess).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "scrcpy kill failed");
            }
        }

        _scrcpyProcess?.Dispose();
        _scrcpyProcess = null;
        IsStreaming = false;
    }

    public Task<byte[]?> CaptureScreenshotAsync(CancellationToken cancellationToken = default) =>
        CapturePngBytesAsync(cancellationToken);

    public async Task SaveScreenshotAsync(string savePath, CancellationToken cancellationToken = default)
    {
        var bytes = await CaptureScreenshotAsync(cancellationToken).ConfigureAwait(false);
        if (bytes is null) return;

        var dir = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllBytesAsync(savePath, bytes, cancellationToken).ConfigureAwait(false);
        _logger.Information("Screenshot saved: {Path}", savePath);
    }

    public Task SendTapAsync(int x, int y, CancellationToken cancellationToken = default) =>
        _adb.ExecuteShellAsync($"input tap {x} {y}", cancellationToken);

    public Task SendSwipeAsync(int x1, int y1, int x2, int y2, int durationMs = 300, CancellationToken cancellationToken = default) =>
        _adb.ExecuteShellAsync($"input swipe {x1} {y1} {x2} {y2} {durationMs}", cancellationToken);

    public Task SendKeyEventAsync(AndroidKeyCode keyCode, CancellationToken cancellationToken = default) =>
        _adb.ExecuteShellAsync($"input keyevent {(int)keyCode}", cancellationToken);

    public Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var escaped = text.Replace(" ", "%s", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("&", "\\&", StringComparison.Ordinal)
            .Replace("<", "\\<", StringComparison.Ordinal)
            .Replace(">", "\\>", StringComparison.Ordinal);
        return _adb.ExecuteShellAsync($"input text \"{escaped}\"", cancellationToken);
    }

    public async Task<DeviceResolution> GetDeviceResolutionAsync(CancellationToken cancellationToken = default)
    {
        var sizeRaw = await _adb.ExecuteShellAsync("wm size", cancellationToken).ConfigureAwait(false);
        var densRaw = await _adb.ExecuteShellAsync("wm density", cancellationToken).ConfigureAwait(false);

        var sizeMatch = SizeRegex().Match(sizeRaw);
        var densMatch = DensityRegex().Match(densRaw);

        return new DeviceResolution
        {
            Width = sizeMatch.Success ? int.Parse(sizeMatch.Groups[1].Value) : 1080,
            Height = sizeMatch.Success ? int.Parse(sizeMatch.Groups[2].Value) : 1920,
            Dpi = densMatch.Success ? int.Parse(densMatch.Groups[1].Value) : 420
        };
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void RefreshToolPaths()
    {
        var settings = _settings.Current;
        _scrcpyPath = PlatformToolsPathResolver.ResolveScrcpyPath(settings.ScrcpyPath);
        _adbPath = PlatformToolsPathResolver.ResolveAdbPath(settings.AdbPath);
    }

    private string? ResolveSerial(string? fromOptions) =>
        !string.IsNullOrWhiteSpace(fromOptions)
            ? fromOptions.Trim()
            : _adb.SelectedDevice?.Serial;

    private async Task<byte[]?> CapturePngBytesAsync(CancellationToken cancellationToken)
    {
        RefreshToolPaths();
        var serial = ResolveSerial(null);
        if (string.IsNullOrWhiteSpace(serial))
            throw new InvalidOperationException("Cihaz seçili değil.");

        if (!File.Exists(_adbPath))
            throw new FileNotFoundException("adb.exe bulunamadı.", _adbPath);

        return await DeviceTransportGate.RunAsync(async outerCt =>
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            timeoutCts.CancelAfter(ProcessWaitHelper.CaptureTimeout);
            var ct = timeoutCts.Token;

            var psi = new ProcessStartInfo
            {
                FileName = _adbPath,
                Arguments = $"-s \"{serial}\" exec-out screencap -p",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi)
                             ?? throw new InvalidOperationException("adb process start failed.");

            await using var killReg = ct.Register(() => ProcessWaitHelper.TryKill(proc));

            var rented = ArrayPool<byte>.Shared.Rent(2 * 1024 * 1024);
            var written = 0;
            try
            {
                var stdout = proc.StandardOutput.BaseStream;
                int read;
                while ((read = await stdout.ReadAsync(rented.AsMemory(written, rented.Length - written), ct)
                           .ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written < rented.Length)
                        continue;

                    var grown = ArrayPool<byte>.Shared.Rent(rented.Length * 2);
                    rented.AsSpan(0, written).CopyTo(grown);
                    ArrayPool<byte>.Shared.Return(rented);
                    rented = grown;
                }

                await proc.WaitForExitAsync(ct).ConfigureAwait(false);

                if (proc.ExitCode != 0 || written == 0)
                {
                    var err = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(err)
                            ? $"screencap başarısız (exit {proc.ExitCode})."
                            : err.Trim());
                }

                var exact = GC.AllocateUninitializedArray<byte>(written);
                rented.AsSpan(0, written).CopyTo(exact);
                return exact;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private ProcessStartInfo CreateScrcpyStartInfo(
        ScrcpyOptions o,
        string serial,
        string adbPath,
        string workDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _scrcpyPath,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = false
        };

        ApplyAdbEnvironment(psi, adbPath);
        psi.Environment["ANDROID_SERIAL"] = serial;

        psi.ArgumentList.Add($"--serial={serial}");
        psi.ArgumentList.Add($"--max-fps={o.MaxFps}");
        psi.ArgumentList.Add($"--video-bit-rate={o.BitRate}M");
        psi.ArgumentList.Add($"--max-size={o.MaxSize}");
        if (o.StayAwake) psi.ArgumentList.Add("--stay-awake");
        if (o.ShowTouches) psi.ArgumentList.Add("--show-touches");
        if (o.NoControl) psi.ArgumentList.Add("--no-control");
        if (o.RecordPath is not null)
            psi.ArgumentList.Add($"--record={o.RecordPath}");

        return psi;
    }

    private static void ApplyAdbEnvironment(ProcessStartInfo psi, string adbPath)
    {
        if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath))
            return;

        var fullAdb = Path.GetFullPath(adbPath);
        psi.Environment["ADB"] = fullAdb;

        var adbDir = Path.GetDirectoryName(fullAdb);
        if (string.IsNullOrWhiteSpace(adbDir))
            return;

        var pathKey = psi.Environment.ContainsKey("PATH") ? "PATH" : "Path";
        var existing = psi.Environment.TryGetValue(pathKey, out var current)
            ? current
            : Environment.GetEnvironmentVariable("PATH") ?? "";
        psi.Environment[pathKey] = adbDir + Path.PathSeparator + existing;
    }

    [GeneratedRegex(@"(\d+)x(\d+)")]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"Physical density:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex DensityRegex();
}
