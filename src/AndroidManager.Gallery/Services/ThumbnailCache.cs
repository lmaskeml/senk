using System.Runtime.Caching;
using System.Windows.Media.Imaging;

namespace AndroidManager.Gallery.Services;

public interface IThumbnailCache
{
    bool TryGet(string key, out BitmapImage? image);
    void Set(string key, BitmapImage image);
    void Clear();
}

/// <summary>
/// In-memory thumbnail bitmap cache (avoids re-decode when scrolling).
/// </summary>
public sealed class ThumbnailCache : IThumbnailCache, IDisposable
{
    private readonly MemoryCache _cache = new("GalleryThumbnails");
    private readonly CacheItemPolicy _policy = new()
    {
        SlidingExpiration = TimeSpan.FromMinutes(10),
        RemovedCallback = OnRemoved
    };

    private static void OnRemoved(CacheEntryRemovedArguments args)
    {
        // BitmapImage held only by cache entry; GC after eviction.
    }

    public bool TryGet(string key, out BitmapImage? image)
    {
        if (_cache.Get(key) is BitmapImage bmp)
        {
            image = bmp;
            return true;
        }

        image = null;
        return false;
    }

    public void Set(string key, BitmapImage image)
    {
        if (!image.IsFrozen)
            image.Freeze();

        _cache.Set(key, image, _policy);
    }

    public void Clear() => _cache.Trim(100);

    public void Dispose() => _cache.Dispose();
}
