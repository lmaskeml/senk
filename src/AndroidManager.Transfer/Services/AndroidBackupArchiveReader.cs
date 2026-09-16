using System.IO;
using System.IO.Compression;
using Serilog;

namespace AndroidManager.Transfer.Services;

/// <summary>
/// Parses Android <c>.ab</c> backup archives (tar + optional deflate) into a local folder.
/// </summary>
public sealed class AndroidBackupArchiveReader
{
    private const string HeaderMagic = "ANDROID BACKUP";

    public async Task<string?> ExtractWhatsAppDatabaseAsync(
        string abFilePath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);

        await using var stream = File.OpenRead(abFilePath);
        using var reader = new StreamReader(stream, leaveOpen: true);

        var headerLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (headerLine != HeaderMagic)
            throw new InvalidDataException("Geçersiz Android yedek dosyası (.ab).");

        var versionLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (!int.TryParse(versionLine, out var version) || version < 1)
            throw new InvalidDataException("Desteklenmeyen .ab sürümü.");

        var compressedLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (!int.TryParse(compressedLine, out var compressed))
            throw new InvalidDataException(".ab sıkıştırma bayrağı okunamadı.");

        var encryptionLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (!int.TryParse(encryptionLine, out var encryption))
            throw new InvalidDataException(".ab şifreleme bayrağı okunamadı.");

        if (encryption != 0)
            throw new InvalidOperationException(
                "WhatsApp .ab yedeği şifreli. Telefonda yedekleme sırasında şifre kullanmayın veya eski WhatsApp APK ile downgrade deneyin.");

        var headerBytesRead = stream.Position;
        Log.Debug("AB header parsed: version={Version} compressed={Compressed} encryption={Encryption} offset={Offset}",
            version, compressed, encryption, headerBytesRead);

        var tarPath = Path.Combine(outputDirectory, "extracted.tar");
        await ExtractPayloadAsync(stream, tarPath, compressed == 1, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(outputDirectory);
        await ExtractTarAsync(tarPath, outputDirectory, cancellationToken).ConfigureAwait(false);

        var candidates = Directory.EnumerateFiles(outputDirectory, "msgstore.db", SearchOption.AllDirectories).ToList();
        if (candidates.Count == 0)
        {
            Log.Warning(
                "msgstore.db not found under {Dir}. Güncel WhatsApp ADB yedeğe veritabanı koymaz — root veya legacy APK gerekir.",
                outputDirectory);
            return null;
        }

        var dbPath = candidates[0];
        var target = Path.Combine(outputDirectory, "msgstore.db");
        File.Copy(dbPath, target, overwrite: true);
        return target;
    }

    private static async Task ExtractPayloadAsync(
        Stream input,
        string tarPath,
        bool compressed,
        CancellationToken cancellationToken)
    {
        await using var output = File.Create(tarPath);

        if (!compressed)
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var deflate = new ZLibStream(input, CompressionMode.Decompress);
        await deflate.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExtractTarAsync(
        string tarPath,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var tar = File.OpenRead(tarPath);
        var buffer = new byte[512];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await tar.ReadAsync(buffer.AsMemory(0, 512), cancellationToken).ConfigureAwait(false);
            if (read < 512)
                break;

            var name = System.Text.Encoding.ASCII.GetString(buffer, 0, 100).TrimEnd('\0');
            if (string.IsNullOrWhiteSpace(name))
                break;

            var sizeOctal = System.Text.Encoding.ASCII.GetString(buffer, 124, 12).Trim('\0', ' ');
            if (!TryParseOctal(sizeOctal, out var fileSize))
                fileSize = 0;

            var typeFlag = (char)buffer[156];
            var fullPath = Path.Combine(destination, name.Replace('/', Path.DirectorySeparatorChar));
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (typeFlag is '5' or '0' && fileSize == 0 && name.EndsWith('/'))
            {
                Directory.CreateDirectory(fullPath.TrimEnd(Path.DirectorySeparatorChar));
            }
            else if (typeFlag is '0' or '\0')
            {
                await using var fileStream = File.Create(fullPath);
                var remaining = fileSize;
                var copyBuffer = new byte[8192];
                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(copyBuffer.Length, remaining);
                    var chunkRead = await tar.ReadAsync(copyBuffer.AsMemory(0, toRead), cancellationToken)
                        .ConfigureAwait(false);
                    if (chunkRead == 0)
                        break;
                    await fileStream.WriteAsync(copyBuffer.AsMemory(0, chunkRead), cancellationToken)
                        .ConfigureAwait(false);
                    remaining -= chunkRead;
                }
            }

            var padding = (512 - (fileSize % 512)) % 512;
            if (padding > 0)
                tar.Seek(padding, SeekOrigin.Current);
        }
    }

    private static bool TryParseOctal(string value, out long result)
    {
        result = 0;
        value = value.Trim();
        if (value.Length == 0)
            return false;

        try
        {
            result = Convert.ToInt64(value, 8);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
