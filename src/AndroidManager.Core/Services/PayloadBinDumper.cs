using System.Buffers;
using System.Buffers.Binary;
using AndroidManager.Core.Models;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;

namespace AndroidManager.Core.Services;

/// <summary>
/// Stream-based payload.bin dumper — harici Python betiği gerektirmez.
/// AOSP update_engine payload v2 (CrAU) formatını destekler.
/// </summary>
public sealed class PayloadBinDumper
{
    private const int DefaultBufferSize = 1024 * 1024;
    private const int ReplaceOp = 0;
    private const int ReplaceBzOp = 1;
    private const int ReplaceXzOp = 8;

    public Task<IReadOnlyList<PayloadPartitionImage>> DumpAsync(
        string payloadBinPath,
        string outputDirectory,
        IProgress<FlashingProgressReport>? progress = null,
        CancellationToken cancellationToken = default) =>
        DumpCoreAsync(payloadBinPath, outputDirectory, partitionFilter: null, progress, cancellationToken);

    /// <summary>Yalnızca istenen partition'ları çıkarır (boot flash finalizer).</summary>
    public Task<IReadOnlyList<PayloadPartitionImage>> DumpPartitionsAsync(
        string payloadBinPath,
        string outputDirectory,
        IReadOnlyCollection<string> partitionNames,
        IProgress<FlashingProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (partitionNames.Count == 0)
            throw new ArgumentException("En az bir partition adı gerekli.", nameof(partitionNames));

        var filter = new HashSet<string>(partitionNames, StringComparer.OrdinalIgnoreCase);
        return DumpCoreAsync(payloadBinPath, outputDirectory, filter, progress, cancellationToken);
    }

    private async Task<IReadOnlyList<PayloadPartitionImage>> DumpCoreAsync(
        string payloadBinPath,
        string outputDirectory,
        HashSet<string>? partitionFilter,
        IProgress<FlashingProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadBinPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        if (!File.Exists(payloadBinPath))
            throw new FileNotFoundException("payload.bin bulunamadı.", payloadBinPath);

        Directory.CreateDirectory(outputDirectory);

        await using var stream = new FileStream(
            payloadBinPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: DefaultBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var header = new byte[20];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);

        if (header[0] != (byte)'C' || header[1] != (byte)'r' || header[2] != (byte)'A' || header[3] != (byte)'U')
            throw new InvalidDataException("Geçersiz payload.bin — CrAU başlığı yok.");

        var fileFormatVersion = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(4));
        var manifestSize = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(12));
        if (manifestSize == 0 || manifestSize > 64 * 1024 * 1024)
            throw new InvalidDataException($"Manifest boyutu geçersiz: {manifestSize}");

        ulong metadataSigSize = 0;
        if (fileFormatVersion >= 2)
        {
            var sigBuf = new byte[4];
            await ReadExactlyAsync(stream, sigBuf, cancellationToken).ConfigureAwait(false);
            metadataSigSize = BinaryPrimitives.ReadUInt32BigEndian(sigBuf);
        }

        var manifestBytes = new byte[manifestSize];
        await ReadExactlyAsync(stream, manifestBytes, cancellationToken).ConfigureAwait(false);

        if (metadataSigSize > 0)
            stream.Seek((long)metadataSigSize, SeekOrigin.Current);

        var payloadDataOffset = stream.Position;
        var manifest = PayloadManifestParser.Parse(manifestBytes);
        if (manifest.Partitions.Count == 0)
            throw new InvalidDataException("payload manifest içinde partition bulunamadı.");

        var results = new List<PayloadPartitionImage>(manifest.Partitions.Count);
        var toProcess = partitionFilter is null
            ? manifest.Partitions
            : manifest.Partitions.Where(p =>
                !string.IsNullOrWhiteSpace(p.PartitionName)
                && partitionFilter.Contains(p.PartitionName)).ToList();

        if (toProcess.Count == 0)
            throw new InvalidDataException(
                partitionFilter is null
                    ? "payload manifest içinde partition bulunamadı."
                    : $"payload içinde istenen partition yok: {string.Join(", ", partitionFilter)}");

        var total = toProcess.Count;
        var index = 0;

        foreach (var partition in toProcess)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;

            if (string.IsNullOrWhiteSpace(partition.PartitionName))
                continue;

            var percent = 10 + (int)Math.Round(index / (double)total * 35);
            progress?.Report(new FlashingProgressReport
            {
                Stage = CustomRomFlashStage.DumpingPayload,
                Percent = percent,
                Message = $"payload çıkarılıyor: {partition.PartitionName}",
                Partition = partition.PartitionName
            });

            var imagePath = Path.Combine(outputDirectory, $"{partition.PartitionName}.img");
            await ExtractPartitionAsync(
                    stream,
                    payloadDataOffset,
                    manifest.BlockSize,
                    partition,
                    imagePath,
                    cancellationToken)
                .ConfigureAwait(false);

            var info = new FileInfo(imagePath);
            if (!info.Exists || info.Length == 0)
                throw new InvalidDataException($"{partition.PartitionName}: çıkarılan imaj boş.");

            results.Add(new PayloadPartitionImage
            {
                PartitionName = partition.PartitionName,
                ImagePath = imagePath,
                SizeBytes = info.Length
            });
        }

        if (results.Count == 0)
            throw new InvalidDataException("payload manifest okundu ancak hiç partition imajı üretilemedi.");

        return results;
    }

    private static async Task ExtractPartitionAsync(
        FileStream payloadStream,
        long payloadDataOffset,
        long blockSize,
        PayloadPartitionUpdate partition,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var replaceOps = partition.Operations
            .Where(o => o.Type is ReplaceOp or ReplaceBzOp or ReplaceXzOp && o.DataLength > 0)
            .ToList();

        if (replaceOps.Count == 0)
            throw new InvalidDataException($"{partition.PartitionName}: desteklenen REPLACE operasyonu yok.");

        ulong maxBlock = 0;
        foreach (var op in replaceOps)
        {
            foreach (var extent in op.DstExtents)
                maxBlock = Math.Max(maxBlock, extent.StartBlock + extent.NumBlocks);
        }

        var fileSize = (long)(maxBlock * (ulong)blockSize);
        if (fileSize <= 0)
            throw new InvalidDataException($"{partition.PartitionName}: çıktı boyutu sıfır.");

        if (File.Exists(outputPath))
            File.Delete(outputPath);

        await using var output = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: DefaultBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        output.SetLength(fileSize);

        foreach (var op in replaceOps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            payloadStream.Seek(payloadDataOffset + (long)op.DataOffset, SeekOrigin.Begin);

            await using var limited = new LimitedReadStream(payloadStream, (long)op.DataLength);
            await using var sink = new ExtentWriteStream(output, op.DstExtents, blockSize, partition.PartitionName);

            if (op.Type == ReplaceOp)
            {
                await PumpAsync(limited, sink, cancellationToken).ConfigureAwait(false);
            }
            else if (op.Type == ReplaceBzOp)
            {
                try
                {
                    await using var bz = BZip2Stream.Create(limited, CompressionMode.Decompress, false);
                    await PumpAsync(bz, sink, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not InvalidDataException)
                {
                    throw new InvalidDataException($"{partition.PartitionName}: bzip2 açma başarısız: {ex.Message}", ex);
                }
            }
            else
            {
                try
                {
                    await using var xz = new XZStream(limited);
                    await PumpAsync(xz, sink, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not InvalidDataException)
                {
                    throw new InvalidDataException($"{partition.PartitionName}: xz açma başarısız: {ex.Message}", ex);
                }
            }

            sink.Complete();
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task PumpAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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

    private sealed class LimitedReadStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        public LimitedReadStream(Stream inner, long length)
        {
            _inner = inner;
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
                return 0;

            var take = (int)Math.Min(count, _remaining);
            var read = _inner.Read(buffer, offset, take);
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0)
                return 0;

            var take = (int)Math.Min(buffer.Length, _remaining);
            var read = await _inner.ReadAsync(buffer[..take], cancellationToken).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ExtentWriteStream : Stream
    {
        private readonly FileStream _output;
        private readonly Queue<(long Offset, long Length)> _extents;
        private readonly string _partitionName;
        private readonly long _expected;
        private long _currentRemaining;
        private long _written;

        public ExtentWriteStream(
            FileStream output,
            IReadOnlyList<PayloadExtent> extents,
            long blockSize,
            string partitionName)
        {
            _output = output;
            _partitionName = partitionName;
            _extents = new Queue<(long, long)>(extents.Count);
            foreach (var extent in extents)
            {
                var length = (long)(extent.NumBlocks * (ulong)blockSize);
                var offset = (long)(extent.StartBlock * (ulong)blockSize);
                _extents.Enqueue((offset, length));
                _expected += length;
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _output.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            var pos = offset;
            var remaining = count;
            while (remaining > 0)
            {
                EnsureExtent();
                var n = (int)Math.Min(remaining, _currentRemaining);
                _output.Write(buffer, pos, n);
                pos += n;
                remaining -= n;
                _currentRemaining -= n;
                _written += n;
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var src = buffer;
            while (!src.IsEmpty)
            {
                EnsureExtent();
                var n = (int)Math.Min(src.Length, _currentRemaining);
                await _output.WriteAsync(src[..n], cancellationToken).ConfigureAwait(false);
                src = src[n..];
                _currentRemaining -= n;
                _written += n;
            }
        }

        private void EnsureExtent()
        {
            if (_currentRemaining > 0)
                return;

            if (_extents.Count == 0)
                throw new InvalidDataException($"{_partitionName}: fazla veri kaldı.");

            var (offset, length) = _extents.Dequeue();
            _output.Seek(offset, SeekOrigin.Begin);
            _currentRemaining = length;
        }

        public void Complete()
        {
            if (_currentRemaining > 0 || _extents.Count > 0 || _written != _expected)
                throw new InvalidDataException($"{_partitionName}: extent için yeterli veri yok.");
        }
    }
}
