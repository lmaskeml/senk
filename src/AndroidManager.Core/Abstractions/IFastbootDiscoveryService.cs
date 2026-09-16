using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

/// <summary>
/// ADB olmadan fastboot/bootloader cihaz keşfi (kurtarma / brick senaryoları).
/// </summary>
public interface IFastbootDiscoveryService
{
    /// <summary>Çözümlenen fastboot.exe yolu; bulunamazsa null veya bare "fastboot".</summary>
    string? ResolvedExecutablePath { get; }

    Task<IReadOnlyList<ConnectedDevice>> GetDevicesAsync(CancellationToken cancellationToken = default);

    Task<string?> GetFirstSerialAsync(CancellationToken cancellationToken = default);

    Task<string?> GetVariableAsync(string serial, string name, CancellationToken cancellationToken = default);
}
