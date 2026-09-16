namespace AndroidManager.Core.Services;

internal static class ProtobufWireWriter
{
    public static byte[] EncodeVarint(ulong value)
    {
        var buffer = new byte[10];
        var pos = 0;
        while (value >= 0x80)
        {
            buffer[pos++] = (byte)(value | 0x80);
            value >>= 7;
        }

        buffer[pos++] = (byte)value;
        return buffer.AsSpan(0, pos).ToArray();
    }

    public static byte[] EncodeTag(int fieldNumber, int wireType) =>
        EncodeVarint((ulong)((fieldNumber << 3) | wireType));

    public static byte[] EncodeLengthDelimitedField(int fieldNumber, ReadOnlySpan<byte> payload)
    {
        var tag = EncodeTag(fieldNumber, 2);
        var len = EncodeVarint((ulong)payload.Length);
        var result = new byte[tag.Length + len.Length + payload.Length];
        var pos = 0;
        tag.CopyTo(result.AsSpan(pos));
        pos += tag.Length;
        len.CopyTo(result.AsSpan(pos));
        pos += len.Length;
        payload.CopyTo(result.AsSpan(pos));
        return result;
    }

    public static byte[] EncodeVarintField(int fieldNumber, ulong value)
    {
        var tag = EncodeTag(fieldNumber, 0);
        var data = EncodeVarint(value);
        var result = new byte[tag.Length + data.Length];
        tag.CopyTo(result.AsSpan(0));
        data.CopyTo(result.AsSpan(tag.Length));
        return result;
    }

    public static byte[] EncodeUtf8StringField(int fieldNumber, string value) =>
        EncodeLengthDelimitedField(fieldNumber, System.Text.Encoding.UTF8.GetBytes(value));

    public static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var result = new byte[total];
        var pos = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result.AsSpan(pos));
            pos += part.Length;
        }

        return result;
    }
}
