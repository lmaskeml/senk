namespace AndroidManager.Core.Services;

internal static class AvbImageHelper
{
    private static ReadOnlySpan<byte> AvbMagic => "AVB0"u8;

    public static bool HasAvbMagic(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return false;

        try
        {
            Span<byte> header = stackalloc byte[4];
            using var stream = File.OpenRead(imagePath);
            return stream.Read(header) == 4 && header.SequenceEqual(AvbMagic);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsAvbMagicError(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && message.Contains("AVB_MAGIC", StringComparison.OrdinalIgnoreCase);
}
