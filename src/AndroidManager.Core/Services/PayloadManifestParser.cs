using AndroidManager.Core.Models;

namespace AndroidManager.Core.Services;

internal static class PayloadManifestParser
{
    public static PayloadManifest Parse(ReadOnlySpan<byte> manifestBytes)
    {
        var manifest = new PayloadManifest();
        var reader = new ProtobufWireReader(manifestBytes);

        while (reader.HasRemaining && reader.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                // major version 1 (legacy)
                case 1 when wire == 2 && reader.TryReadLengthDelimited(out var legacyPartBytes):
                    manifest.Partitions.Add(ParsePartitionUpdate(legacyPartBytes));
                    break;
                // major version >= 2 (crDroid / modern OTA)
                case 13 when wire == 2 && reader.TryReadLengthDelimited(out var partBytes):
                    manifest.Partitions.Add(ParsePartitionUpdate(partBytes));
                    break;
                case 3 when wire == 0 && reader.TryReadVarint(out var blockSize):
                    manifest.BlockSize = blockSize > 0 ? (long)blockSize : 4096;
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return manifest;
    }

    private static PayloadPartitionUpdate ParsePartitionUpdate(ReadOnlySpan<byte> bytes)
    {
        var update = new PayloadPartitionUpdate();
        var reader = new ProtobufWireReader(bytes);

        while (reader.HasRemaining && reader.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2 && reader.TryReadLengthDelimited(out var nameBytes):
                    update.PartitionName = ProtobufWireReader.ReadUtf8String(nameBytes);
                    break;
                case 8 when wire == 2 && reader.TryReadLengthDelimited(out var opBytes):
                    update.Operations.Add(ParseInstallOperation(opBytes));
                    break;
                // legacy v1 manifests used field 2 for operations
                case 2 when wire == 2 && reader.TryReadLengthDelimited(out var legacyOpBytes):
                    update.Operations.Add(ParseInstallOperation(legacyOpBytes));
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return update;
    }

    private static PayloadInstallOperation ParseInstallOperation(ReadOnlySpan<byte> bytes)
    {
        var op = new PayloadInstallOperation();
        var reader = new ProtobufWireReader(bytes);

        while (reader.HasRemaining && reader.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 0 && reader.TryReadVarint(out var type):
                    op.Type = (int)type;
                    break;
                case 2 when wire == 0 && reader.TryReadVarint(out var offset):
                    op.DataOffset = offset;
                    break;
                case 3 when wire == 0 && reader.TryReadVarint(out var length):
                    op.DataLength = length;
                    break;
                case 6 when wire == 2 && reader.TryReadLengthDelimited(out var extentBytes):
                    op.DstExtents.Add(ParseExtent(extentBytes));
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return op;
    }

    private static PayloadExtent ParseExtent(ReadOnlySpan<byte> bytes)
    {
        var extent = new PayloadExtent();
        var reader = new ProtobufWireReader(bytes);

        while (reader.HasRemaining && reader.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 0 && reader.TryReadVarint(out var start):
                    extent.StartBlock = start;
                    break;
                case 2 when wire == 0 && reader.TryReadVarint(out var num):
                    extent.NumBlocks = num;
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return extent;
    }
}
