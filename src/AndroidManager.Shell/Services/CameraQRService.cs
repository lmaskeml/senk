using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using AndroidManager.Core;
using Serilog;
using ZXing;
using ZXing.Common;
using ZXing.Windows.Compatibility;
using DrawingBitmap = System.Drawing.Bitmap;

namespace AndroidManager.Shell.Services;

public interface ICameraQRService : IDisposable
{
    bool IsCameraAvailable { get; }
    event EventHandler<BitmapSource>? FrameCaptured;
    event EventHandler<string>? QRDecoded;

    Task StartAsync(int cameraIndex = 0, CancellationToken cancellationToken = default);
    void Stop();
    Task<string?> DecodeFromFileAsync(string filePath, CancellationToken cancellationToken = default);
    Task<string?> DecodeFromClipboardAsync();
}

public sealed class CameraQRService : ICameraQRService
{
    private readonly ILogger _logger;
    private readonly BarcodeReader _reader;
    private VideoCapture? _capture;
    private CancellationTokenSource? _loopCts;
    private int _frameCounter;
    private bool _disposed;

    public bool IsCameraAvailable { get; private set; }

    public event EventHandler<BitmapSource>? FrameCaptured;
    public event EventHandler<string>? QRDecoded;

    public CameraQRService(ILogger? logger = null)
    {
        _logger = logger ?? Log.ForContext<CameraQRService>();
        _reader = new BarcodeReader
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                PossibleFormats = [BarcodeFormat.QR_CODE]
            }
        };
        ProbeCamera();
    }

    private void ProbeCamera()
    {
        try
        {
            using var test = new VideoCapture(0);
            IsCameraAvailable = test.IsOpened();
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Camera probe failed");
            IsCameraAvailable = false;
        }
    }

    public Task StartAsync(int cameraIndex = 0, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsCameraAvailable)
            throw new InvalidOperationException("Kamera bulunamadı");

        Stop();

        _capture = new VideoCapture(cameraIndex);
        if (!_capture.IsOpened())
        {
            _capture.Dispose();
            _capture = null;
            throw new InvalidOperationException("Kamera açılamadı");
        }

        _capture.Set(VideoCaptureProperties.FrameWidth, 640);
        _capture.Set(VideoCaptureProperties.FrameHeight, 480);

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _loopCts.Token;
        ObservedTask.Run(Task.Run(() => CaptureLoopAsync(ct), ct));
        return Task.CompletedTask;
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        using var frame = new Mat();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var capture = _capture;
                if (capture is null || !capture.Read(frame) || frame.Empty())
                {
                    await Task.Delay(50, ct);
                    continue;
                }

                using var bitmap = BitmapConverter.ToBitmap(frame);
                var source = ToBitmapSource(bitmap);
                FrameCaptured?.Invoke(this, source);

                _frameCounter++;
                if (_frameCounter % 3 == 0)
                {
                    var decoded = _reader.Decode(bitmap);
                    if (decoded is { Text: { Length: > 0 } text })
                    {
                        _logger.Information("QR decoded from camera");
                        QRDecoded?.Invoke(this, text);
                        Stop();
                        break;
                    }
                }

                await Task.Delay(33, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Camera frame error");
                await Task.Delay(100, ct);
            }
        }
    }

    public Task<string?> DecodeFromFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                using var bitmap = new DrawingBitmap(filePath);
                return _reader.Decode(bitmap)?.Text;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "QR file decode failed: {Path}", filePath);
                return null;
            }
        }, cancellationToken);
    }

    public Task<string?> DecodeFromClipboardAsync()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return Task.FromResult<string?>(null);

        return dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (!System.Windows.Clipboard.ContainsImage())
                    return null;

                var image = System.Windows.Clipboard.GetImage();
                if (image is null)
                    return null;

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                ms.Position = 0;
                using var bitmap = new DrawingBitmap(ms);
                return _reader.Decode(bitmap)?.Text;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Clipboard QR decode failed");
                return null;
            }
        }).Task;
    }

    private static BitmapSource ToBitmapSource(DrawingBitmap bitmap)
    {
        var hBitmap = bitmap.GetHbitmap();
        try
        {
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    public void Stop()
    {
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _loopCts = null;
        _capture?.Dispose();
        _capture = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
