using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Gallery.Imaging;
using MaterialDesignThemes.Wpf;

namespace AndroidManager.Gallery.Views;

public partial class GalleryPreviewWindow : Window
{
    private readonly GalleryMediaItem _item;
    private CancellationTokenSource? _loadCts;
    private readonly DispatcherTimer _positionTimer;
    private bool _isSeeking;
    private bool _isPlaying;

    public GalleryPreviewWindow(GalleryMediaItem item)
    {
        InitializeComponent();
        _item = item;
        Title = item.DisplayName;
        InfoText.Text = $"{item.DisplayName} · {item.SizeFormatted} · indiriliyor…";

        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _positionTimer.Tick += (_, _) => UpdatePositionUi();
    }

    public async Task LoadAsync(IGalleryService gallery, CancellationToken cancellationToken = default)
    {
        _loadCts?.Cancel();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _loadCts.Token;

        try
        {
            if (_item.Kind == GalleryMediaKind.Video
                || LooksLikeVideo(_item.RemotePath, _item.MimeType))
            {
                InfoText.Text = $"{_item.DisplayName} · video indiriliyor…";
                var path = await gallery.PullOriginalAsync(_item, token).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                ImageViewer.Visibility = Visibility.Collapsed;
                VideoPlayer.Visibility = Visibility.Visible;
                VideoControls.Visibility = Visibility.Visible;
                VideoPlayer.Source = new Uri(path);
                VideoPlayer.Play();
                _isPlaying = true;
                PlayPauseIcon.Kind = PackIconKind.Pause;
                _positionTimer.Start();
                InfoText.Text = $"{_item.DisplayName} · {_item.SizeFormatted}";
                return;
            }

            var local = await gallery.PullOriginalAsync(_item, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();

            if (!GalleryImageCodec.IsLikelyRasterImageFile(local))
            {
                InfoText.Text =
                    $"{_item.DisplayName} · indirme bozuk ({new FileInfo(local).Length} bayt). Yenileyip tekrar deneyin.";
                return;
            }

            InfoText.Text = $"{_item.DisplayName} · çözülüyor…";
            var bmp = await Task.Run(() => GalleryImageCodec.LoadAsBitmapImage(local, decodePixelWidth: 1600), token)
                .ConfigureAwait(true);

            VideoPlayer.Visibility = Visibility.Collapsed;
            VideoControls.Visibility = Visibility.Collapsed;
            ImageViewer.Visibility = Visibility.Visible;
            ImageViewer.Source = bmp;
            InfoText.Text = $"{_item.DisplayName} · {_item.SizeFormatted} · {_item.DateTakenLocal:g}";
        }
        catch (OperationCanceledException)
        {
            InfoText.Text = "İptal edildi";
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            if (msg.Length > 140)
                msg = msg[..140] + "…";
            InfoText.Text = $"{_item.DisplayName} · {msg}";
        }
    }

    private void VideoPlayer_OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (VideoPlayer.NaturalDuration.HasTimeSpan)
            SeekSlider.Maximum = VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds;
        UpdatePositionUi();
    }

    private void VideoPlayer_OnMediaEnded(object sender, RoutedEventArgs e)
    {
        _isPlaying = false;
        PlayPauseIcon.Kind = PackIconKind.Play;
        _positionTimer.Stop();
    }

    private void PlayPause_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            VideoPlayer.Pause();
            _isPlaying = false;
            PlayPauseIcon.Kind = PackIconKind.Play;
            _positionTimer.Stop();
        }
        else
        {
            VideoPlayer.Play();
            _isPlaying = true;
            PlayPauseIcon.Kind = PackIconKind.Pause;
            _positionTimer.Start();
        }
    }

    private void StopVideo_OnClick(object sender, RoutedEventArgs e)
    {
        VideoPlayer.Stop();
        _isPlaying = false;
        PlayPauseIcon.Kind = PackIconKind.Play;
        SeekSlider.Value = 0;
        _positionTimer.Stop();
        UpdatePositionUi();
    }

    private void SeekBack_OnClick(object sender, RoutedEventArgs e)
    {
        var pos = VideoPlayer.Position - TimeSpan.FromSeconds(10);
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        VideoPlayer.Position = pos;
        UpdatePositionUi();
    }

    private void SeekForward_OnClick(object sender, RoutedEventArgs e)
    {
        var pos = VideoPlayer.Position + TimeSpan.FromSeconds(10);
        if (VideoPlayer.NaturalDuration.HasTimeSpan && pos > VideoPlayer.NaturalDuration.TimeSpan)
            pos = VideoPlayer.NaturalDuration.TimeSpan;
        VideoPlayer.Position = pos;
        UpdatePositionUi();
    }

    private void SeekSlider_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _isSeeking = true;

    private void SeekSlider_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        VideoPlayer.Position = TimeSpan.FromSeconds(SeekSlider.Value);
        _isSeeking = false;
        UpdatePositionUi();
    }

    private void VolumeSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VideoPlayer is not null)
            VideoPlayer.Volume = e.NewValue;
    }

    private void UpdatePositionUi()
    {
        if (!_isSeeking)
            SeekSlider.Value = VideoPlayer.Position.TotalSeconds;

        var pos = VideoPlayer.Position;
        var dur = VideoPlayer.NaturalDuration.HasTimeSpan
            ? VideoPlayer.NaturalDuration.TimeSpan
            : TimeSpan.Zero;
        TimeText.Text = $"{pos:mm\\:ss} / {dur:mm\\:ss}";
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    private void Window_OnClosed(object? sender, EventArgs e)
    {
        _positionTimer.Stop();
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        try
        {
            VideoPlayer.Stop();
            VideoPlayer.Source = null;
        }
        catch
        {
            // ignore
        }
    }

    private static bool LooksLikeVideo(string path, string mime)
    {
        if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return true;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mp4" or ".mkv" or ".webm" or ".3gp" or ".avi" or ".mov";
    }
}
