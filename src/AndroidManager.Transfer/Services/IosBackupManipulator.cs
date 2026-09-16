using System.Security.Cryptography;
using System.Text;
using AndroidManager.Core.Models;
using Microsoft.Data.Sqlite;
using Serilog;

namespace AndroidManager.Transfer.Services;

/// <summary>
/// Places converted WhatsApp files into an iTunes backup (hashed fileID paths) and updates Manifest.db.
/// </summary>
public sealed class IosBackupManipulator
{
    private const string WhatsAppSharedDomain =
        "AppDomainGroup-group.net.whatsapp.WhatsApp.shared";

    private readonly ILogger _logger;

    public IosBackupManipulator(ILogger? logger = null) =>
        _logger = logger ?? Log.ForContext<IosBackupManipulator>();

    public async Task<(int MediaCopied, IReadOnlyList<string> Warnings)> InjectWhatsAppDataAsync(
        string backupDirectory,
        string chatStoragePath,
        string? androidMediaDirectory,
        IProgress<WhatsAppTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(backupDirectory))
            throw new DirectoryNotFoundException($"iOS yedek klasörü bulunamadı: {backupDirectory}");

        if (!File.Exists(chatStoragePath))
            throw new FileNotFoundException("ChatStorage.sqlite bulunamadı.", chatStoragePath);

        var manifestPath = Path.Combine(backupDirectory, "Manifest.db");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException(
                "Manifest.db bulunamadı. Önce iPhone'dan şifresiz bir iTunes yedeği oluşturun.");

        var warnings = new List<string>();
        Report(progress, WhatsAppTransferPhase.InjectingIos, 20, "ChatStorage.sqlite yerleştiriliyor…");

        await UpsertBackupFileAsync(
                backupDirectory,
                manifestPath,
                WhatsAppSharedDomain,
                "ChatStorage.sqlite",
                chatStoragePath,
                cancellationToken)
            .ConfigureAwait(false);

        var mediaCopied = 0;
        if (!string.IsNullOrWhiteSpace(androidMediaDirectory) && Directory.Exists(androidMediaDirectory))
        {
            Report(progress, WhatsAppTransferPhase.InjectingIos, 50, "Medya dosyaları kopyalanıyor…");
            foreach (var file in Directory.EnumerateFiles(androidMediaDirectory, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativeWithinMedia = Path.GetRelativePath(androidMediaDirectory, file).Replace('\\', '/');
                var iosRelativePath = $"Message/Media/{relativeWithinMedia}";
                await UpsertBackupFileAsync(
                        backupDirectory,
                        manifestPath,
                        WhatsAppSharedDomain,
                        iosRelativePath,
                        file,
                        cancellationToken)
                    .ConfigureAwait(false);
                mediaCopied++;
            }

            _logger.Information("Copied {Count} media files into iOS backup", mediaCopied);
        }
        else
        {
            warnings.Add("Android medya klasörü yok — yalnızca metin mesajları aktarıldı.");
        }

        Report(progress, WhatsAppTransferPhase.InjectingIos, 90, "Manifest.db güncellendi (binary plist).");
        return (mediaCopied, warnings);
    }

    private static async Task UpsertBackupFileAsync(
        string backupDirectory,
        string manifestPath,
        string domain,
        string relativePath,
        string sourceFilePath,
        CancellationToken cancellationToken)
    {
        var fileId = IosManifestFileBlobBuilder.ComputeFileId(domain, relativePath);
        var targetPath = IosManifestFileBlobBuilder.GetHashedBackupPath(backupDirectory, fileId);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.Copy(sourceFilePath, targetPath, overwrite: true);

        var (size, sha1) = ComputeFileMetadata(targetPath);
        var blob = IosManifestFileBlobBuilder.Build(domain, relativePath, size, sha1);

        await using var connection = new SqliteConnection($"Data Source={manifestPath}");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM Files WHERE fileID = $id OR (domain = $domain AND relativePath = $relative)";
            delete.Parameters.AddWithValue("$id", fileId);
            delete.Parameters.AddWithValue("$domain", domain);
            delete.Parameters.AddWithValue("$relative", relativePath);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO Files (fileID, domain, relativePath, flags, file)
            VALUES ($id, $domain, $relative, 1, $blob)
            """;
        insert.Parameters.AddWithValue("$id", fileId);
        insert.Parameters.AddWithValue("$domain", domain);
        insert.Parameters.AddWithValue("$relative", relativePath);
        insert.Parameters.AddWithValue("$blob", blob);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static (long Size, byte[] Sha1) ComputeFileMetadata(string path)
    {
        var info = new FileInfo(path);
        using var stream = File.OpenRead(path);
        var hash = SHA1.HashData(stream);
        return (info.Length, hash);
    }

    private static void Report(
        IProgress<WhatsAppTransferProgress>? progress,
        WhatsAppTransferPhase phase,
        int percent,
        string message) =>
        progress?.Report(new WhatsAppTransferProgress { Phase = phase, Percent = percent, Message = message });
}
