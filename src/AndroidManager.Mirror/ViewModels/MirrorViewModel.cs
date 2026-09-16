using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Mirror.Converters;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AndroidManager.Mirror.ViewModels;

public sealed partial class MirrorViewModel : ObservableObject, IDisposable
{
    private readonly IMirrorService _mirror;
    private readonly IAppDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly ISettingsService _settings;
    private DeviceResolution? _resolution;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private BitmapSource? _currentFrame;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private bool _isScrcpyMode;
    [ObservableProperty] private string _statusMessage = "Hazır";
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string? _recordPath;

    [ObservableProperty] private int _maxFps;
    [ObservableProperty] private int _bitRate;
    [ObservableProperty] private int _maxSize;
    [ObservableProperty] private bool _stayAwake;
    [ObservableProperty] private bool _showTouches;
    [ObservableProperty] private bool _noControl;
    [ObservableProperty] private int _screencapFps = 10;

    public bool IsScrcpyAvailable => _mirror.IsScrcpyAvailable;

    public MirrorViewModel(
        IMirrorService mirror,
        IAppDialogService dialogs,
        IUiDispatcher dispatcher,
        ISettingsService settings)
    {
        _mirror = mirror;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _settings = settings;
        _mirror.FrameReceived += OnFrameReceived;
        _mirror.ErrorOccurred += OnError;
        ApplySettings(_settings.Current);
        IsScrcpyMode = IsScrcpyAvailable;
        _settings.SettingsChanged += (_, s) =>
        {
            _dispatcher.Observe(() =>
            {
                ApplySettings(s);
                if (!IsScrcpyAvailable && IsScrcpyMode)
                    IsScrcpyMode = false;
            });
        };
    }

    private void ApplySettings(AppSettings s)
    {
        MaxFps = s.MirrorFps;
        BitRate = s.MirrorBitRate;
        MaxSize = s.MirrorMaxSize;
        StayAwake = s.MirrorStayAwake;
    }

    [RelayCommand]
    private async Task StartMirrorAsync()
    {
        _cts = new CancellationTokenSource();
        try
        {
            if (IsScrcpyMode && IsScrcpyAvailable)
            {
                StatusMessage = "scrcpy başlatılıyor...";
                var opts = BuildScrcpyOptions();
                await _mirror.StartScrcpyAsync(opts, _cts.Token);
                MirrorReconnectCoordinator.NotifyStarted(opts);
                StatusMessage = "scrcpy çalışıyor (ayrı pencerede)";
            }
            else
            {
                StatusMessage = $"Screencap stream başlatılıyor ({ScreencapFps} FPS)...";
                await _mirror.StartScreencapStreamAsync(ScreencapFps, _cts.Token);
                StatusMessage = $"Stream aktif — {ScreencapFps} FPS";
            }

            IsStreaming = true;
            _resolution = await _mirror.GetDeviceResolutionAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Mirror start failed");
            StatusMessage = ex.Message;
            IsStreaming = false;
            await _mirror.StopAsync();
        }
    }

    [RelayCommand]
    private async Task StopMirrorAsync()
    {
        if (_cts is not null)
            await _cts.CancelAsync();

        await _mirror.StopAsync();
        IsStreaming = false;
        IsRecording = false;
        CurrentFrame = null;
        StatusMessage = "Durduruldu";
    }

    [RelayCommand]
    private async Task TakeScreenshotAsync()
    {
        var path = await _dialogs.PickSaveFileAsync(
            "Ekran görüntüsü kaydet",
            "PNG|*.png|JPEG|*.jpg",
            $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        if (string.IsNullOrWhiteSpace(path)) return;

        StatusMessage = "Screenshot alınıyor...";
        try
        {
            await _mirror.SaveScreenshotAsync(path);
            StatusMessage = $"Kaydedildi: {path}";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ToggleRecordAsync()
    {
        if (!IsScrcpyAvailable)
        {
            StatusMessage = "Kayıt için scrcpy gerekli";
            return;
        }

        if (!IsRecording)
        {
            var path = await _dialogs.PickSaveFileAsync(
                "Ekran kaydı",
                "MP4|*.mp4",
                $"record_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
            if (string.IsNullOrWhiteSpace(path)) return;

            RecordPath = path;
            IsRecording = true;
            StatusMessage = $"Kaydediliyor: {Path.GetFileName(RecordPath)}";

            _cts = new CancellationTokenSource();
            var opts = new ScrcpyOptions
            {
                MaxFps = MaxFps,
                BitRate = BitRate,
                MaxSize = MaxSize,
                StayAwake = StayAwake,
                ShowTouches = ShowTouches,
                NoControl = NoControl,
                RecordPath = RecordPath
            };

            try
            {
                await _mirror.StartScrcpyAsync(opts, _cts.Token);
                IsStreaming = true;
            }
            catch (Exception ex)
            {
                IsRecording = false;
                StatusMessage = ex.Message;
            }
        }
        else
        {
            await StopMirrorAsync();
            StatusMessage = $"Kayıt tamamlandı: {RecordPath}";
            RecordPath = null;
        }
    }

    [RelayCommand]
    private async Task SendKeyAsync(AndroidKeyCode key) =>
        await _mirror.SendKeyEventAsync(key);

    public async Task HandleImageClickAsync(Point clickPoint, Size imageSize)
    {
        if (_resolution is null || imageSize.Width <= 0 || imageSize.Height <= 0)
            return;

        var scaleX = _resolution.Width / imageSize.Width;
        var scaleY = _resolution.Height / imageSize.Height;
        await _mirror.SendTapAsync((int)(clickPoint.X * scaleX), (int)(clickPoint.Y * scaleY));
    }

    public async Task HandleSwipeAsync(Point start, Point end, Size imageSize, int durationMs = 300)
    {
        if (_resolution is null || imageSize.Width <= 0 || imageSize.Height <= 0)
            return;

        var scaleX = _resolution.Width / imageSize.Width;
        var scaleY = _resolution.Height / imageSize.Height;
        await _mirror.SendSwipeAsync(
            (int)(start.X * scaleX), (int)(start.Y * scaleY),
            (int)(end.X * scaleX), (int)(end.Y * scaleY),
            durationMs);
    }

    private void OnFrameReceived(object? sender, byte[] pngBytes)
    {
        var bmp = BitmapFactory.FromPngBytes(pngBytes);
        if (bmp is null) return;
        _dispatcher.Observe(() => CurrentFrame = bmp);
    }

    private void OnError(object? sender, MirrorErrorEventArgs e) =>
        _dispatcher.Observe(() => StatusMessage = e.Message);

    private ScrcpyOptions BuildScrcpyOptions() => new()
    {
        MaxFps = MaxFps,
        BitRate = BitRate,
        MaxSize = MaxSize,
        StayAwake = StayAwake,
        ShowTouches = ShowTouches,
        NoControl = NoControl
    };

    public void Dispose()
    {
        _mirror.FrameReceived -= OnFrameReceived;
        _mirror.ErrorOccurred -= OnError;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
        _cts = null;
    }
}
