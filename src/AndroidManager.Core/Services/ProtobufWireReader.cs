namespace AndroidManager.Core.Services;

/// <summary>Minimal protobuf wire reader for AOSP payload manifests (no code-gen dependency).</summary>
internal ref struct ProtobufWireReader
{
    private ReadOnlySpan<byte> _buffer;
    private int _pos;

    public ProtobufWireReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _pos = 0;
    }

    public bool HasRemaining => _pos < _buffer.Length;

    public bool TryReadTag(out int fieldNumber, out int wireType)
    {
        fieldNumber = 0;
        wireType = 0;
        if (!TryReadVarint(out var tag))
            return false;

        wireType = (int)(tag & 0x7);
        fieldNumber = (int)(tag >> 3);
        return fieldNumber > 0;
    }

    public bool TryReadVarint(out ulong value)
    {
        value = 0;
        var shift = 0;
        while (_pos < _buffer.Length)
        {
            var b = _buffer[_pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return true;
            shift += 7;
            if (shift > 63)
                return false;
        }

        return false;
    }

    public bool TryReadLengthDelimited(out ReadOnlySpan<byte> slice)
    {
        slice = default;
        if (!TryReadVarint(out var len) || len > int.MaxValue)
            return false;

        var length = (int)len;
        if (_pos + length > _buffer.Length)
            return false;

        slice = _buffer.Slice(_pos, length);
        _pos += length;
        return true;
    }

    public void SkipField(int wireType)
    {
        switch (wireType)
        {
            case 0:
                _ = TryReadVarint(out _);
                break;
            case 1:
                _pos = Math.Min(_pos + 8, _buffer.Length);
                break;
            case 2:
                if (TryReadVarint(out var len))
                    _pos = Math.Min(_pos + (int)len, _buffer.Length);
                break;
            case 5:
                _pos = Math.Min(_pos + 4, _buffer.Length);
                break;
        }
    }

    public static string ReadUtf8String(ReadOnlySpan<byte> bytes) =>
        System.Text.Encoding.UTF8.GetString(bytes);
}
