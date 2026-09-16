using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface IGalleryService
{
    Task<IReadOnlyList<GalleryMediaItem>> GetMediaAsync(
        GalleryMediaKind? kind = null,
        int limit = 1000,
        CancellationToken cancellationToken = default,
        MediaSortOrder sort = MediaSortOrder.DateDesc);

    /// <summary>Returns a local JPEG/PNG thumbnail path (cached).</summary>
    Task<string?> GetThumbnailPathAsync(
        GalleryMediaItem item,
        int maxEdge = 256,
        CancellationToken cancellationToken = default);

    /// <summary>Pulls the original file to a temp/cache path for preview or playback.</summary>
    Task<string> PullOriginalAsync(
        GalleryMediaItem item,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MediaAlbum>> GetAlbumsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GalleryMediaItem>> GetAlbumItemsAsync(
        string albumPath,
        CancellationToken cancellationToken = default);

    Task SaveToLocalAsync(
        IEnumerable<GalleryMediaItem> items,
        string localDir,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool convertImagesToJpeg = false);

    /// <summary>
    /// If the file is a raster image other than JPEG, re-encodes to .jpg next to it and removes the original.
    /// Returns the final path (unchanged for JPEG/video/non-images).
    /// </summary>
    Task<string> EnsureLocalJpegAsync(
        string localPath,
        CancellationToken cancellationToken = default);
}
