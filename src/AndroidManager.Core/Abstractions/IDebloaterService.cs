using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

/// <summary>
/// System / OEM debloat operations (non-root: disable-user / uninstall --user 0).
/// Existing <see cref="IAppService"/> still exposes the legacy Disable/Restore APIs.
/// </summary>
public interface IDebloaterService
{
    Task<IReadOnlyList<DebloatCandidate>> GetDebloatableAppsAsync(
        CancellationToken cancellationToken = default);

    Task<DebloatResult> DisableAppAsync(
        string packageName,
        CancellationToken cancellationToken = default);

    Task<DebloatResult> EnableAppAsync(
        string packageName,
        CancellationToken cancellationToken = default);

    Task<DebloatResult> UninstallForUserAsync(
        string packageName,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DebloatCandidate>> GetDisabledAppsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DebloatCategory>> GetCategoriesAsync(
        CancellationToken cancellationToken = default);
}
