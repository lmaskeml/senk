using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface IBackupService
{
    Task<BackupJob> CreateBackupAsync(
        BackupOptions options,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<RestoreResult> RestoreBackupAsync(
        string backupPath,
        RestoreOptions options,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>ZIP veya klasör yedekteki uygulamaları (APK + veri) listeler; arşivi tam açmaz.</summary>
    Task<IReadOnlyList<BackupAppEntry>> InspectBackupAppsAsync(
        string backupPath,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackupJob>> GetBackupHistoryAsync(CancellationToken cancellationToken = default);
    Task DeleteBackupAsync(int jobId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SmsMessage>> GetSmsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Contact>> GetContactsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MediaFile>> GetPhotosAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MediaFile>> GetVideosAsync(CancellationToken cancellationToken = default);
}
