namespace AndroidManager.Core.Abstractions;

/// <summary>
/// Cross-module root (su) komut yüzeyi. Backup gibi Security’ye referans vermeyen
/// modüller uygulama verisi yedekleme için bunu kullanır.
/// </summary>
public interface IElevatedShellService
{
    /// <summary>su mevcut ve uid=0 doğrulandı mı?</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>Root olarak komut çalıştırır; root yoksa boş string döner.</summary>
    Task<string> RunAsync(
        string command,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}
