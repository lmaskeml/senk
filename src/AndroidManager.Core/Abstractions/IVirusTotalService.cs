using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface IVirusTotalService
{
    VtConfig Config { get; }

    Task<bool> SaveConfigAsync(VtConfig config, CancellationToken cancellationToken = default);

    /// <summary>Hash sorgula (MD5/SHA-1/SHA-256). Sonuç cache'lenir.</summary>
    Task<VtReport> CheckHashAsync(string hash, CancellationToken cancellationToken = default);

    /// <summary>Toplu doğrulama (sıralı, kota limitlerine uyar).</summary>
    Task<List<VtReport>> CheckBatchAsync(
        IEnumerable<string> hashes,
        IProgress<VtProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Yerel dosyayı VirusTotal'e yükler, analizi bekler ve raporu döner.
    /// &gt;32MB dosyalar için presigned upload_url kullanılır.
    /// </summary>
    Task<VtReport?> UploadAndScanAsync(
        string localFilePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    Task<VtStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}
