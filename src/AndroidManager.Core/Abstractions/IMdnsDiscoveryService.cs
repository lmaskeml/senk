using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

/// <summary>
/// Raw multicast mDNS listener for <c>_adb-tls-connect</c> / <c>_adb-tls-pairing</c>.
/// Prefer <see cref="IWirelessDebugDiscoveryService"/> for production Wi‑Fi pairing
/// (uses <c>adb mdns services</c>).
/// </summary>
public interface IMdnsDiscoveryService : IDisposable
{
    event EventHandler<MdnsDevice>? DeviceFound;
    event EventHandler<string>? DeviceLost;

    Task StartAsync(CancellationToken cancellationToken = default);
    void Stop();
    IReadOnlyList<MdnsDevice> GetCurrentDevices();
}
