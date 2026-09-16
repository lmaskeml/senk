using System.IO;
using System.Windows.Media.Imaging;
using AndroidManager.Core;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Gallery.Imaging;
using AndroidManager.Gallery.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AndroidManager.Gallery.ViewModels;

public sealed partial class GalleryItemViewModel : ObservableObject
{
    private readonly IGalleryService _gallery;
    private readonly IUiDispatcher _dispatcher;
    private readonly IThumbnailCache _thumbCache;
    private int _thumbRequest;
    private int _loadStarted;

    public GalleryMediaItem Item { get; }

    [ObservableProperty] private BitmapImage? _thumbnail;
    [ObservableProperty] private bool _isLoadingThumbnail;
    [ObservableProperty] private bool _thumbnailFailed;

    public GalleryItemViewModel(
        GalleryMediaItem item,
        IGalleryService gallery,
        IUiDispatcher dispatcher,
        IThumbnailCache thumbCache)
    {
        Item = item;
        _gallery = gallery;
        _dispatcher = dispatcher;
        _thumbCache = thumbCache;
    }

    public string Title => Item.DisplayName;
    public string Subtitle
    {
        get
        {
            var parts = new List<string> { Item.SizeFormatted, Item.DateTakenLocal.ToString("g") };
            if (!string.IsNullOrWhiteSpace(Item.Album))
                parts.Insert(0, Item.Album);
            if (IsVideo && !string.IsNullOrWhiteSpace(Item.DurationFormatted))
                parts.Add(Item.DurationFormatted);
            return string.Join(" · ", parts);
        }
    }
    public bool IsVideo => Item.Kind == GalleryMediaKind.Video;
    public string KindLabel => IsVideo ? "Video" : "Fotoğraf";
    public string Duration => Item.DurationFormatted;
    public string Album => Item.Album;

    public void EnsureThumbnailRequested()
    {
        if (Thumbnail is not null || ThumbnailFailed)
            return;

        var cacheKey = Item.RemotePath;
        if (!string.IsNullOrWhiteSpace(cacheKey) && _thumbCache.TryGet(cacheKey, out var cached) && cached is not null)
        {
            Thumbnail = cached;
            return;
        }

        if (Interlocked.CompareExchange(ref _loadStarted, 1, 0) != 0)
            return;

        ObservedTask.Run(LoadThumbnailAsync());
    }

    private async Task LoadThumbnailAsync()
    {
        var request = Interlocked.Increment(ref _thumbRequest);
        await _dispatcher.InvokeAsync(() => IsLoadingThumbnail = true);

        try
        {
            var cacheKey = Item.RemotePath;
            if (!string.IsNullOrWhiteSpace(cacheKey) && _thumbCache.TryGet(cacheKey, out var cached) && cached is not null)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    if (request != _thumbRequest) return;
                    Thumbnail = cached;
                    IsLoadingThumbnail = false;
                });
                return;
            }

            var path = await _gallery.GetThumbnailPathAsync(Item).ConfigureAwait(false);
            if (request != _thumbRequest)
                return;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    ThumbnailFailed = true;
                    IsLoadingThumbnail = false;
                });
                return;
            }

            // Decode off UI thread (WebP → JPEG → BitmapImage).
            BitmapImage? bmp = null;
            try
            {
                bmp = await Task.Run(() => GalleryImageCodec.LoadAsBitmapImage(path, decodePixelWidth: 256))
                    .ConfigureAwait(false);
            }
            catch
            {
                bmp = null;
            }

            if (bmp is not null && !string.IsNullOrWhiteSpace(cacheKey))
                _thumbCache.Set(cacheKey, bmp);

            await _dispatcher.InvokeAsync(() =>
            {
                if (request != _thumbRequest)
                    return;

                if (bmp is null)
                    ThumbnailFailed = true;
                else
                    Thumbnail = bmp;

                IsLoadingThumbnail = false;
            });
        }
        catch
        {
            await _dispatcher.InvokeAsync(() =>
            {
                ThumbnailFailed = true;
                IsLoadingThumbnail = false;
            });
        }
    }
}
