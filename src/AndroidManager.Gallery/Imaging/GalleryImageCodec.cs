using System.IO;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace AndroidManager.Gallery.Imaging;

/// <summary>
/// Fast JPEG/PNG via WPF; WebP via ImageSharp. Validates ADB payloads are real images.
/// </summary>
internal static class GalleryImageCodec
{
    private static readonly JpegEncoder JpegThumbEncoder = new() { Quality = 72 };
    private static readonly JpegEncoder JpegExportEncoder = new() { Quality = 92 };

    public static async Task WriteJpegThumbnailAsync(
        string sourcePath,
        string destPath,
        int maxEdge,
        CancellationToken cancellationToken = default)
    {
        if (!IsLikelyRasterImageFile(sourcePath))
            throw new InvalidOperationException("Kaynak dosya geçerli bir görüntü değil (bozuk veya ADB hata çıktısı).");

        // JPEG/PNG: WPF decode+encode is enough and often faster for thumbs.
        if (IsJpegOrPngHeader(sourcePath))
        {
            await Task.Run(() => WriteThumbWithWpf(sourcePath, destPath, maxEdge), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await using var input = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var image = await Image.LoadAsync(input, cancellationToken).ConfigureAwait(false);

        var edge = Math.Max(16, maxEdge);
        if (image.Width > edge || image.Height > edge)
        {
            image.Mutate(ctx => ctx
                .AutoOrient()
                .Resize(new ResizeOptions
                {
                    Size = new Size(edge, edge),
                    Mode = ResizeMode.Max,
                    Sampler = KnownResamplers.Box
                }));
        }
        else
        {
            image.Mutate(ctx => ctx.AutoOrient());
        }

        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        await using var output = new FileStream(
            destPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        await image.SaveAsJpegAsync(output, JpegThumbEncoder, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Full-resolution JPEG export (WebP/GIF/PNG/BMP → JPG). No downscale.</summary>
    public static async Task WriteFullJpegAsync(
        string sourcePath,
        string destPath,
        CancellationToken cancellationToken = default)
    {
        if (!IsLikelyRasterImageFile(sourcePath))
            throw new InvalidOperationException("Kaynak dosya geçerli bir görüntü değil.");

        var ext = Path.GetExtension(sourcePath);
        if (ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                File.Copy(sourcePath, destPath, overwrite: true);
            return;
        }

        await using var input = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var image = await Image.LoadAsync(input, cancellationToken).ConfigureAwait(false);
        image.Mutate(ctx => ctx.AutoOrient());

        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        await using var output = new FileStream(
            destPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        await image.SaveAsJpegAsync(output, JpegExportEncoder, cancellationToken).ConfigureAwait(false);
    }

    public static bool NeedsJpegConversion(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".webp" or ".png" or ".gif" or ".bmp" or ".tif" or ".tiff";
    }

    /// <summary>
    /// Preview load: JPEG/PNG use WPF DecodePixelWidth (no full re-encode). WebP uses ImageSharp once.
    /// </summary>
    public static BitmapImage LoadAsBitmapImage(string path, int? decodePixelWidth = null)
    {
        if (!IsLikelyRasterImageFile(path))
            throw new InvalidOperationException(
                "Dosya görüntü değil — muhtemelen ADB indirmesi başarısız (önbellek bozuk). Yenile / tekrar dene.");

        var maxEdge = decodePixelWidth is > 0 ? decodePixelWidth.Value : 1920;

        if (IsJpegOrPngHeader(path))
            return LoadWithWpf(path, maxEdge);

        // WebP / GIF — ImageSharp, but never decode above preview edge.
        using var image = Image.Load(path);
        if (image.Width > maxEdge || image.Height > maxEdge)
        {
            image.Mutate(ctx => ctx
                .AutoOrient()
                .Resize(new ResizeOptions
                {
                    Size = new Size(maxEdge, maxEdge),
                    Mode = ResizeMode.Max,
                    Sampler = KnownResamplers.Box
                }));
        }
        else
        {
            image.Mutate(ctx => ctx.AutoOrient());
        }

        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, JpegThumbEncoder);
        ms.Position = 0;
        return CreateBitmapFromStream(ms);
    }

    private static void WriteThumbWithWpf(string sourcePath, string destPath, int maxEdge)
    {
        var bmp = LoadWithWpf(sourcePath, maxEdge);
        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 72 };
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        using var fs = File.Create(destPath);
        encoder.Save(fs);
    }

    private static BitmapImage LoadWithWpf(string path, int decodePixelWidth)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bmp.UriSource = new Uri(path);
        if (decodePixelWidth > 0)
            bmp.DecodePixelWidth = decodePixelWidth;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static BitmapImage CreateBitmapFromStream(Stream stream)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bmp.StreamSource = stream;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public static bool LooksLikeRasterImage(string path, string? mime)
    {
        if (!string.IsNullOrWhiteSpace(mime)
            && mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && !mime.Contains("heic", StringComparison.OrdinalIgnoreCase)
            && !mime.Contains("heif", StringComparison.OrdinalIgnoreCase))
            return true;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp" or ".tif" or ".tiff";
    }

    public static bool IsLikelyRasterImageFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;

            var length = new FileInfo(path).Length;
            if (length < 24)
                return false;

            Span<byte> header = stackalloc byte[16];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var read = fs.Read(header);
            if (read < 12)
                return false;

            if (header[0] == (byte)'c' && header[1] == (byte)'a' && header[2] == (byte)'t' && header[3] == (byte)':')
                return false;
            if (header[0] == (byte)'P' && header[1] == (byte)'e' && header[2] == (byte)'r' && header[3] == (byte)'m')
                return false;

            if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
                return true;
            if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                return true;
            if (header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46)
                return true;
            if (header[0] == 0x42 && header[1] == 0x4D)
                return true;
            if (header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46
                && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
                return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsJpegOrPngHeader(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[4];
            using var fs = File.OpenRead(path);
            if (fs.Read(header) < 3)
                return false;
            if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
                return true;
            return header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47;
        }
        catch
        {
            return false;
        }
    }
}
