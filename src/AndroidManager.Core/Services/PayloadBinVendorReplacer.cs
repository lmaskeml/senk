using System.Buffers;
using System.Buffers.Binary;
using AndroidManager.Core.Models;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;

namespace AndroidManager.Core.Services;

/// <summary>payload.bin içinde yalnızca vendor partition blob'unu değiştirir — hazır sideload zip için.</summary>
internal sealed class PayloadBinVendorReplacer
{
    private const int ReplaceBzOp = 1;
    private const int PartitionField = 13;

    public async Task<string> ReplaceVendorAsync(
        string payloadBinPath,
        string patchedVendorImagePath,
        string outputPayloadPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadBinPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(patchedVendorImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPayloadPath);

        if (!File.Exists(payloadBinPath))
            throw new FileNotFoundException("payload.bin bulunamadı.", payloadBinPath);
        if (!File.Exists(patchedVendorImagePath))
            throw new FileNotFoundException("vendor.img bulunamadı.", patchedVendorImagePath);

        await using var input = new FileStream(
            payloadBinPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var header = new byte[20];
        await ReadExactlyAsync(input, header, cancellationToken).ConfigureAwait(false);

        if (header[0] != (byte)'C' || header[1] != (byte)'r' || header[2] != (byte)'A' || header[3] != (byte)'U')
            throw new InvalidDataException("Geçersiz payload.bin — CrAU başlığı yok.");

        var fileFormatVersion = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(4));
        var manifestSize = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(12));

        ulong metadataSigSize = 0;
        if (fileFormatVersion >= 2)
        {
            var sigBuf = new byte[4];
            await ReadExactlyAsync(input, sigBuf, cancellationToken).ConfigureAwait(false);
            metadataSigSize = BinaryPrimitives.ReadUInt32BigEndian(sigBuf);
        }

        var manifestBytes = new byte[manifestSize];
        await ReadExactlyAsync(input, manifestBytes, cancellationToken).ConfigureAwait(false);

        byte[]? metadataSignature = null;
        if (metadataSigSize > 0)
        {
            metadataSignature = new byte[metadataSigSize];
            await ReadExactlyAsync(input, metadataSignature, cancellationToken).ConfigureAwait(false);
        }

        var payloadDataOffset = input.Position;
        var manifest = PayloadManifestParser.Parse(manifestBytes);
        if (manifest.Partitions.Count == 0)
            throw new InvalidDataException("payload manifest boş.");

        var vendorIndex = manifest.Partitions.FindIndex(p =>
            p.PartitionName.Equals("vendor", StringComparison.OrdinalIgnoreCase));
        if (vendorIndex < 0)
            throw new InvalidDataException("payload içinde vendor partition yok.");

        var vendorTemp = Path.Combine(Path.GetTempPath(), $"am-vendor-{Guid.NewGuid():N}.bz2");
        try
        {
            await CompressVendorImageAsync(
                    patchedVendorImagePath,
                    manifest.BlockSize,
                    manifest.Partitions[vendorIndex],
                    vendorTemp,
                    cancellationToken)
                .ConfigureAwait(false);

            var lengths = new long[manifest.Partitions.Count];
            for (var i = 0; i < manifest.Partitions.Count; i++)
            {
                lengths[i] = i == vendorIndex
                    ? new FileInfo(vendorTemp).Length
                    : manifest.Partitions[i].Operations
                        .Where(o => o.DataLength > 0)
                        .Sum(o => (long)o.DataLength);
            }

            var newManifestBytes = BuildManifestBytes(manifest, lengths, vendorIndex);
            var newHeader = BuildHeader(fileFormatVersion, (ulong)newManifestBytes.Length, metadataSigSize);

            var outputDir = Path.GetDirectoryName(outputPayloadPath);
            if (!string.IsNullOrWhiteSpace(outputDir))
                Directory.CreateDirectory(outputDir);

            if (File.Exists(outputPayloadPath))
                File.Delete(outputPayloadPath);

            await using var output = new FileStream(
                outputPayloadPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            await output.WriteAsync(newHeader, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(newManifestBytes, cancellationToken).ConfigureAwait(false);
            if (metadataSignature is not null)
                await output.WriteAsync(metadataSignature, cancellationToken).ConfigureAwait(false);

            var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            try
            {
                for (var i = 0; i < manifest.Partitions.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (i == vendorIndex)
                    {
                        await CopyFileToStreamAsync(vendorTemp, output, buffer, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await CopyPartitionOpsAsync(
                                input,
                                payloadDataOffset,
                                manifest.Partitions[i],
                                output,
                                buffer,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return outputPayloadPath;
        }
        finally
        {
            try
            {
                if (File.Exists(vendorTemp))
                    File.Delete(vendorTemp);
            }
            catch
            {
                // leftover temp
            }
        }
    }

    private static async Task CompressVendorImageAsync(
        string vendorImagePath,
        long blockSize,
        PayloadPartitionUpdate originalVendor,
        string outputBz2Path,
        CancellationToken cancellationToken)
    {
        await using var img = new FileStream(
            vendorImagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (img.Length == 0)
            throw new InvalidDataException("vendor.img boş.");

        if (blockSize <= 0)
            blockSize = 4096;

        var expectedBlocks = originalVendor.Operations
            .SelectMany(o => o.DstExtents)
            .Aggregate(0UL, (sum, e) => sum + e.NumBlocks);
        var expectedSize = expectedBlocks > 0
            ? (long)(expectedBlocks * (ulong)blockSize)
            : img.Length;

        var take = Math.Min(img.Length, expectedSize);
        var pad = expectedSize - take;

        await using var dest = new FileStream(
            outputBz2Path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await using (var bz = BZip2Stream.Create(dest, CompressionMode.Compress, false))
        {
            await CopyExactlyAsync(img, bz, take, cancellationToken).ConfigureAwait(false);
            if (pad > 0)
            {
                var zeros = ArrayPool<byte>.Shared.Rent(1024 * 1024);
                Array.Clear(zeros);
                try
                {
                    var remaining = pad;
                    while (remaining > 0)
                    {
                        var n = (int)Math.Min(remaining, zeros.Length);
                        await bz.WriteAsync(zeros.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                        remaining -= n;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(zeros);
                }
            }

            bz.Finish();
        }
    }

    private static byte[] BuildManifestBytes(
        PayloadManifest manifest,
        IReadOnlyList<long> partitionLengths,
        int vendorIndex)
    {
        var chunks = new List<byte[]>
        {
            ProtobufWireWriter.EncodeVarintField(3, (ulong)manifest.BlockSize)
        };

        ulong globalDataOffset = 0;
        for (var i = 0; i < manifest.Partitions.Count; i++)
        {
            var partition = manifest.Partitions[i];
            var blobLength = partitionLengths[i];
            byte[] encodedPartition;

            if (i == vendorIndex)
            {
                var numBlocks = partition.Operations
                    .SelectMany(o => o.DstExtents)
                    .Aggregate(0UL, (sum, e) => sum + e.NumBlocks);
                if (numBlocks == 0)
                    throw new InvalidDataException("vendor partition block sayısı okunamadı.");

                var vendorOp = BuildInstallOperation(
                    ReplaceBzOp,
                    globalDataOffset,
                    (ulong)blobLength,
                    0,
                    numBlocks);
                encodedPartition = EncodePartitionUpdate(partition.PartitionName, [vendorOp]);
            }
            else
            {
                var ops = RemapOperations(partition.Operations, globalDataOffset);
                encodedPartition = EncodePartitionUpdate(partition.PartitionName, ops);
            }

            chunks.Add(ProtobufWireWriter.EncodeLengthDelimitedField(PartitionField, encodedPartition));
            globalDataOffset += (ulong)blobLength;
        }

        return ProtobufWireWriter.Concat(chunks.ToArray());
    }

    private static List<PayloadInstallOperation> RemapOperations(
        IReadOnlyList<PayloadInstallOperation> originalOps,
        ulong partitionBaseOffset)
    {
        var ordered = originalOps
            .Where(o => o.DataLength > 0)
            .OrderBy(o => o.DataOffset)
            .ToList();

        var ops = new List<PayloadInstallOperation>(ordered.Count);
        ulong localOffset = 0;
        foreach (var op in ordered)
        {
            var remapped = new PayloadInstallOperation
            {
                Type = op.Type,
                DataOffset = partitionBaseOffset + localOffset,
                DataLength = op.DataLength
            };
            remapped.DstExtents.AddRange(op.DstExtents.Select(e => new PayloadExtent
            {
                StartBlock = e.StartBlock,
                NumBlocks = e.NumBlocks
            }));
            ops.Add(remapped);
            localOffset += op.DataLength;
        }

        return ops;
    }

    private static async Task CopyPartitionOpsAsync(
        FileStream input,
        long payloadDataOffset,
        PayloadPartitionUpdate partition,
        Stream output,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var ops = partition.Operations
            .Where(o => o.DataLength > 0)
            .OrderBy(o => o.DataOffset)
            .ToList();

        foreach (var op in ops)
        {
            input.Seek(payloadDataOffset + (long)op.DataOffset, SeekOrigin.Begin);
            await CopyExactlyAsync(input, output, (long)op.DataLength, buffer, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task CopyFileToStreamAsync(
        string path,
        Stream output,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await CopyExactlyAsync(input, output, input.Length, buffer, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyExactlyAsync(
        Stream source,
        Stream dest,
        long count,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            await CopyExactlyAsync(source, dest, count, buffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task CopyExactlyAsync(
        Stream source,
        Stream dest,
        long count,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var remaining = count;
        while (remaining > 0)
        {
            var n = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), cancellationToken)
                .ConfigureAwait(false);
            if (n == 0)
                throw new EndOfStreamException("payload.bin beklenenden kısa.");
            await dest.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
            remaining -= n;
        }
    }

    private static PayloadInstallOperation BuildInstallOperation(
        int type,
        ulong dataOffset,
        ulong dataLength,
        ulong startBlock,
        ulong numBlocks)
    {
        var op = new PayloadInstallOperation
        {
            Type = type,
            DataOffset = dataOffset,
            DataLength = dataLength
        };
        op.DstExtents.Add(new PayloadExtent { StartBlock = startBlock, NumBlocks = numBlocks });
        return op;
    }

    private static byte[] EncodePartitionUpdate(string name, IReadOnlyList<PayloadInstallOperation> operations)
    {
        var chunks = new List<byte[]> { ProtobufWireWriter.EncodeUtf8StringField(1, name) };
        foreach (var op in operations)
            chunks.Add(ProtobufWireWriter.EncodeLengthDelimitedField(8, EncodeInstallOperation(op)));

        return ProtobufWireWriter.Concat(chunks.ToArray());
    }

    private static byte[] EncodeInstallOperation(PayloadInstallOperation op)
    {
        var chunks = new List<byte[]>
        {
            ProtobufWireWriter.EncodeVarintField(1, (ulong)op.Type),
            ProtobufWireWriter.EncodeVarintField(2, op.DataOffset),
            ProtobufWireWriter.EncodeVarintField(3, op.DataLength)
        };

        foreach (var extent in op.DstExtents)
            chunks.Add(ProtobufWireWriter.EncodeLengthDelimitedField(6, EncodeExtent(extent)));

        return ProtobufWireWriter.Concat(chunks.ToArray());
    }

    private static byte[] EncodeExtent(PayloadExtent extent) =>
        ProtobufWireWriter.Concat(
            ProtobufWireWriter.EncodeVarintField(1, extent.StartBlock),
            ProtobufWireWriter.EncodeVarintField(2, extent.NumBlocks));

    private static byte[] BuildHeader(ulong fileFormatVersion, ulong manifestSize, ulong metadataSigSize)
    {
        var header = new byte[20];
        header[0] = (byte)'C';
        header[1] = (byte)'r';
        header[2] = (byte)'A';
        header[3] = (byte)'U';
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(4), fileFormatVersion);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(12), manifestSize);

        if (fileFormatVersion < 2)
            return header;

        var sig = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(sig, (uint)metadataSigSize);
        return ProtobufWireWriter.Concat(header, sig);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("payload.bin beklenenden kısa.");
            offset += read;
        }
    }
}
