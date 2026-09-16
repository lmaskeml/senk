using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface IBankRootHideService
{
    Task<BankRootHideAudit> AuditAsync(CancellationToken cancellationToken = default);

    Task<BankRootHideActionResult> ClearYkbAppDataAsync(CancellationToken cancellationToken = default);

    Task<BankRootHideActionResult> ClearGmsCacheAsync(CancellationToken cancellationToken = default);

    Task<BankRootHideActionResult> ApplyDenyListLayerAsync(CancellationToken cancellationToken = default);

    /// <summary>Parasitik bildirim calismazsa manager.apk kurar (zip veya cihazdan).</summary>
    Task<BankRootHideActionResult> InstallLsposedManagerApkAsync(CancellationToken cancellationToken = default);
}
