using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface IRomBackupVaultService
{
    Task<RomBackupCapability> ProbeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DevicePartitionInfo>> ListPartitionsAsync(CancellationToken cancellationToken = default);

    Task<RomBackupSession> CreateBackupAsync(
        RomBackupRequest request,
        IProgress<RomBackupProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RomBackupSession>> ListSessionsAsync(CancellationToken cancellationToken = default);

    Task<RomBackupSession?> LoadSessionAsync(string folder, CancellationToken cancellationToken = default);

    Task<DeviceToolResult> RestoreAsync(
        RomBackupRestoreRequest request,
        IProgress<RomBackupProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
