using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface IMagiskDenyListService
{
    Task<MagiskDenyListSnapshot> ListAsync(CancellationToken cancellationToken = default);

    Task<bool> AddAsync(string package, string? process = null, CancellationToken cancellationToken = default);

    /// <summary>Shamiko için Enforce DenyList'i kapatır.</summary>
    Task<bool> SetEnforceAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// TR bankalar + YKB tüm süreçler → denylist (CLI + sqlite) ve boot pin modülü kurar.
    /// Magisk UI tik'ine güvenilmez; YKB satırına tıklayıp alt süreçleri işaretlemek gerekir.
    /// </summary>
    Task<MagiskDenyListPinResult> PinBankPackagesAsync(CancellationToken cancellationToken = default);
}
