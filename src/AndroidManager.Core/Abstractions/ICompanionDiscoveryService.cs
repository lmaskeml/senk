using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface ICompanionDiscoveryService : IDisposable
{
    event EventHandler<CompanionDevice>? DeviceDiscovered;

    Task StartListeningAsync(CancellationToken cancellationToken = default);
    void StopListening();

    /// <summary>Recently seen Companion heartbeats (for endpoint resolution).</summary>
    IReadOnlyList<CompanionDevice> GetCachedDevices();

    Task<CompanionConnectResult> ConnectWithPairCodeAsync(
        string ip,
        int tcpPort,
        string pairCode,
        string pcName,
        CancellationToken cancellationToken = default);

    Task<CompanionConnectResult> ConnectFromQrAsync(
        string qrContent,
        CancellationToken cancellationToken = default);
}
