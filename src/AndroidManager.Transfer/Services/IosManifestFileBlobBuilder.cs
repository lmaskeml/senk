using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace AndroidManager.Transfer.Services;

/// <summary>
/// Builds NSKeyedArchiver binary plist blobs for iTunes backup Manifest.db Files.file column (MBFile).
/// </summary>
internal static class IosManifestFileBlobBuilder
{
    private const double AppleReferenceEpoch = 978_307_200;

    public static byte[] Build(string domain, string relativePath, long size, byte[] sha1Digest)
    {
        if (sha1Digest.Length != 20)
            throw new ArgumentException("SHA-1 digest must be 20 bytes.", nameof(sha1Digest));

        var appleDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)AppleReferenceEpoch;
        var graph = BuildObjectGraph(domain, relativePath, size, sha1Digest, appleDate);
        return BinaryPlistEncoder.Encode(graph);
    }

    public static string ComputeFileId(string domain, string relativePath)
    {
        var key = domain + "-" + relativePath;
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    }

    public static string GetHashedBackupPath(string backupDirectory, string fileId) =>
        Path.Combine(backupDirectory, fileId[..2], fileId);

    private static PlistNode BuildObjectGraph(
        string domain,
        string relativePath,
        long size,
        byte[] sha1Digest,
        long appleDate)
    {
        const int idxNull = 0;
        const int idxRoot = 1;
        const int idxDomain = 2;
        const int idxRelative = 3;
        const int idxMbFileClass = 4;
        const int idxRootClass = 5;
        const int idxArchiver = 6;
        const int idxVersion = 7;
        const int idxTop = 8;

        var objects = new[]
        {
            PlistNode.Null(),
            PlistNode.Dict(new Dictionary<string, PlistNode>
            {
                ["$class"] = PlistNode.Uid(idxRootClass),
                ["Birth"] = PlistNode.Date(appleDate),
                ["Digest"] = PlistNode.Data(sha1Digest),
                ["Domain"] = PlistNode.Uid(idxDomain),
                ["Flags"] = PlistNode.Int(1),
                ["GroupID"] = PlistNode.Int(501),
                ["InodeNumber"] = PlistNode.Int(0),
                ["LastModified"] = PlistNode.Date(appleDate),
                ["Mode"] = PlistNode.Int(33188),
                ["ProtectionClass"] = PlistNode.Int(2),
                ["RelativePath"] = PlistNode.Uid(idxRelative),
                ["Size"] = PlistNode.Int(size),
                ["UserID"] = PlistNode.Int(501),
            }),
            PlistNode.Str(domain),
            PlistNode.Str(relativePath),
            PlistNode.Dict(new Dictionary<string, PlistNode>
            {
                ["$classes"] = PlistNode.Array([PlistNode.Str("MBFile"), PlistNode.Str("NSObject")]),
                ["$classname"] = PlistNode.Str("MBFile"),
            }),
            PlistNode.Dict(new Dictionary<string, PlistNode>
            {
                ["$classes"] = PlistNode.Array([PlistNode.Str("NSDictionary"), PlistNode.Str("NSObject")]),
                ["$classname"] = PlistNode.Str("NSDictionary"),
            }),
            PlistNode.Str("NSKeyedArchiver"),
            PlistNode.Int(100_000),
            PlistNode.Dict(new Dictionary<string, PlistNode>
            {
                ["root"] = PlistNode.Uid(idxRoot),
            }),
        };

        return PlistNode.Dict(new Dictionary<string, PlistNode>
        {
            ["$archiver"] = PlistNode.Uid(idxArchiver),
            ["$objects"] = PlistNode.Array(objects),
            ["$top"] = PlistNode.Uid(idxTop),
            ["$version"] = PlistNode.Uid(idxVersion),
        });
    }

    private enum PlistNodeKind { Null, Int, Date, Data, Str, Uid, Array, Dict }

    private sealed class PlistNode
    {
        public PlistNodeKind Kind { get; init; }
        public long IntValue { get; init; }
        public byte[]? Bytes { get; init; }
        public string? Text { get; init; }
        public PlistNode[]? Children { get; init; }
        public Dictionary<string, PlistNode>? Entries { get; init; }

        public static PlistNode Null() => new() { Kind = PlistNodeKind.Null };
        public static PlistNode Int(long v) => new() { Kind = PlistNodeKind.Int, IntValue = v };
        public static PlistNode Date(long appleSeconds) => new() { Kind = PlistNodeKind.Date, IntValue = appleSeconds };
        public static PlistNode Data(byte[] d) => new() { Kind = PlistNodeKind.Data, Bytes = d };
        public static PlistNode Str(string s) => new() { Kind = PlistNodeKind.Str, Text = s };
        public static PlistNode Uid(int index) => new() { Kind = PlistNodeKind.Uid, IntValue = index };
        public static PlistNode Array(IReadOnlyList<PlistNode> items) => new() { Kind = PlistNodeKind.Array, Children = items.ToArray() };
        public static PlistNode Dict(Dictionary<string, PlistNode> items) => new() { Kind = PlistNodeKind.Dict, Entries = items };
    }

    private static class BinaryPlistEncoder
    {
        public static byte[] Encode(PlistNode root)
        {
            var objects = new List<PlistNode>();
            var indexMap = new Dictionary<PlistNode, int>(ReferenceEqualityComparer.Instance);
            var stringNodes = new Dictionary<string, PlistNode>(StringComparer.Ordinal);
            AssignIndex(root, objects, indexMap, stringNodes);

            using var ms = new MemoryStream();
            ms.Write("bplist00"u8);

            var offsets = new int[objects.Count];
            for (var i = 0; i < objects.Count; i++)
            {
                offsets[i] = (int)ms.Length;
                WriteObject(ms, objects[i], indexMap, stringNodes);
            }

            var offsetIntSize = offsets.Max() <= byte.MaxValue ? 1 : offsets.Max() <= ushort.MaxValue ? 2 : 4;
            var offsetTablePos = (int)ms.Length;
            foreach (var offset in offsets)
                WriteSizedInt(ms, offset, offsetIntSize);

            var trailer = new byte[32];
            trailer[6] = (byte)offsetIntSize;
            trailer[7] = 1;
            BinaryPrimitives.WriteInt64BigEndian(trailer.AsSpan(8), objects.Count);
            BinaryPrimitives.WriteInt64BigEndian(trailer.AsSpan(16), indexMap[root]);
            BinaryPrimitives.WriteInt64BigEndian(trailer.AsSpan(24), offsetTablePos);
            ms.Write(trailer);
            return ms.ToArray();
        }

        private static void AssignIndex(
            PlistNode node,
            List<PlistNode> objects,
            Dictionary<PlistNode, int> map,
            Dictionary<string, PlistNode> stringNodes)
        {
            if (map.ContainsKey(node))
                return;

            map[node] = objects.Count;
            objects.Add(node);

            switch (node.Kind)
            {
                case PlistNodeKind.Array:
                    if (node.Children is { } arrayChildren)
                    {
                        foreach (var child in arrayChildren)
                            AssignIndex(child, objects, map, stringNodes);
                    }
                    break;
                case PlistNodeKind.Dict:
                    if (node.Entries is { } dictEntries)
                    {
                        foreach (var key in dictEntries.Keys.OrderBy(static k => k, StringComparer.Ordinal))
                        {
                            if (!stringNodes.TryGetValue(key, out var keyNode))
                            {
                                keyNode = PlistNode.Str(key);
                                stringNodes[key] = keyNode;
                            }

                            AssignIndex(keyNode, objects, map, stringNodes);
                            AssignIndex(dictEntries[key], objects, map, stringNodes);
                        }
                    }
                    break;
            }
        }

        private static void WriteObject(
            Stream s,
            PlistNode node,
            IReadOnlyDictionary<PlistNode, int> map,
            IReadOnlyDictionary<string, PlistNode> stringNodes)
        {
            switch (node.Kind)
            {
                case PlistNodeKind.Null:
                    s.WriteByte(0x00);
                    break;
                case PlistNodeKind.Int:
                    WriteInteger(s, node.IntValue);
                    break;
                case PlistNodeKind.Date:
                    s.WriteByte(0x33);
                    var epoch = node.IntValue + (long)AppleReferenceEpoch;
                    Span<byte> dateBuf = stackalloc byte[8];
                    BinaryPrimitives.WriteDoubleBigEndian(dateBuf, epoch);
                    s.Write(dateBuf);
                    break;
                case PlistNodeKind.Data:
                    WriteBytesWithPrefix(s, 0x4, node.Bytes ?? []);
                    break;
                case PlistNodeKind.Str:
                    WriteString(s, node.Text ?? string.Empty);
                    break;
                case PlistNodeKind.Uid:
                    s.WriteByte(0x80);
                    s.WriteByte((byte)node.IntValue);
                    break;
                case PlistNodeKind.Array:
                    WriteCountPrefix(s, 0xA, node.Children?.Length ?? 0);
                    if (node.Children is { } children)
                    {
                        foreach (var child in children)
                            s.WriteByte((byte)map[child]);
                    }
                    break;
                case PlistNodeKind.Dict:
                    if (node.Entries is not { } entries)
                        break;
                    var keys = entries.Keys.OrderBy(static k => k, StringComparer.Ordinal).ToArray();
                    WriteCountPrefix(s, 0xD, keys.Length);
                    foreach (var key in keys)
                    {
                        s.WriteByte((byte)map[stringNodes[key]]);
                        s.WriteByte((byte)map[entries[key]]);
                    }
                    break;
            }
        }

        private static void WriteInteger(Stream s, long value)
        {
            if (value is >= 0 and <= byte.MaxValue)
            {
                s.WriteByte(0x10);
                s.WriteByte((byte)value);
                return;
            }

            if (value is >= short.MinValue and <= short.MaxValue)
            {
                s.WriteByte(0x11);
                Span<byte> buf = stackalloc byte[2];
                BinaryPrimitives.WriteInt16BigEndian(buf, (short)value);
                s.Write(buf);
                return;
            }

            if (value is >= int.MinValue and <= int.MaxValue)
            {
                s.WriteByte(0x12);
                Span<byte> buf = stackalloc byte[4];
                BinaryPrimitives.WriteInt32BigEndian(buf, (int)value);
                s.Write(buf);
                return;
            }

            s.WriteByte(0x13);
            Span<byte> longBuf = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(longBuf, value);
            s.Write(longBuf);
        }

        private static void WriteString(Stream s, string text)
        {
            var ascii = Encoding.UTF8.GetBytes(text);
            if (ascii.All(static b => b < 128))
            {
                WriteBytesWithPrefix(s, 0x5, ascii);
                return;
            }

            WriteBytesWithPrefix(s, 0x6, Encoding.BigEndianUnicode.GetBytes(text));
        }

        private static void WriteBytesWithPrefix(Stream s, int marker, byte[] data)
        {
            if (data.Length < 15)
            {
                s.WriteByte((byte)((marker << 4) | data.Length));
            }
            else
            {
                s.WriteByte((byte)((marker << 4) | 0xF));
                s.WriteByte((byte)data.Length);
            }

            s.Write(data);
        }

        private static void WriteCountPrefix(Stream s, int marker, int count)
        {
            if (count < 15)
            {
                s.WriteByte((byte)((marker << 4) | count));
                return;
            }

            s.WriteByte((byte)((marker << 4) | 0xF));
            s.WriteByte((byte)count);
        }

        private static void WriteSizedInt(Stream s, int value, int size)
        {
            Span<byte> buf = stackalloc byte[8];
            buf.Clear();
            BinaryPrimitives.WriteInt32BigEndian(buf[(size - 4)..], value);
            s.Write(buf[..size]);
        }
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<PlistNode>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();
        public bool Equals(PlistNode? x, PlistNode? y) => ReferenceEquals(x, y);
        public int GetHashCode(PlistNode obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
