using System.Buffers.Binary;

namespace AndroidManager.Core.Services;

public enum VendorImageFormat
{
    Unknown,
    Ext4,
    Erofs
}

public static class VendorImageFormatProbe
{
    private const ushort Ext4Magic = 0xEF53;
    private const uint ErofsMagic = 0xE0F5E1E2;

    public static VendorImageFormat Detect(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return VendorImageFormat.Unknown;

        Span<byte> header = stackalloc byte[4096];
        using var stream = File.OpenRead(imagePath);
        var read = stream.Read(header);
        if (read < 1082)
            return VendorImageFormat.Unknown;

        if (BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(1024)) == ErofsMagic)
            return VendorImageFormat.Erofs;

        if (BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(1080)) == Ext4Magic)
            return VendorImageFormat.Ext4;

        return VendorImageFormat.Unknown;
    }
}
