using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AndroidManager.Core.Abstractions;

namespace AndroidManager.Security.Engines;

public static class HashHelper
{
    public static async Task<(string Md5, string Sha256)> HashFileAsync(
        string localPath,
        CancellationToken cancellationToken = default)
    {
        // Tüm dosyayı belleğe almak yerine stream üzerinden hashle — büyük APK/IMG için GC baskısını düşürür.
        await using var stream = new FileStream(
            localPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

#pragma warning disable CA5351
        using var md5 = MD5.Create();
#pragma warning restore CA5351
        using var sha256 = SHA256.Create();

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                md5.TransformBlock(buffer, 0, read, null, 0);
                sha256.TransformBlock(buffer, 0, read, null, 0);
            }

            md5.TransformFinalBlock([], 0, 0);
            sha256.TransformFinalBlock([], 0, 0);

            return (
                Convert.ToHexString(md5.Hash!).ToLowerInvariant(),
                Convert.ToHexString(sha256.Hash!).ToLowerInvariant());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async Task<(string Md5, string Sha256)> HashRemoteAsync(
        IAdbService adb,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        var sha = await adb.ExecuteShellAsync($"sha256sum \"{remotePath}\" 2>/dev/null", cancellationToken)
            .ConfigureAwait(false);
        var md5 = await adb.ExecuteShellAsync($"md5sum \"{remotePath}\" 2>/dev/null", cancellationToken)
            .ConfigureAwait(false);

        return (FirstToken(md5), FirstToken(sha));
    }

    public static string FirstToken(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var line = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        var token = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return Regex.IsMatch(token, "^[a-fA-F0-9]{16,}$") ? token.ToLowerInvariant() : "";
    }

    public static string ExtractAsciiStrings(byte[] data, int minLen = 6)
    {
        var sb = new StringBuilder();
        Span<char> chunk = stackalloc char[256];
        var len = 0;

        foreach (var b in data)
        {
            if (b is >= 32 and < 127)
            {
                if (len < chunk.Length)
                    chunk[len++] = (char)b;
                else
                {
                    // Taşan uzun string'i yine de yaz; excess'i atla.
                    if (len >= minLen)
                        sb.Append(chunk[..len]).Append(' ');
                    len = 0;
                }
            }
            else
            {
                if (len >= minLen)
                    sb.Append(chunk[..len]).Append(' ');
                len = 0;
            }
        }

        if (len >= minLen)
            sb.Append(chunk[..len]);
        return sb.ToString();
    }
}
