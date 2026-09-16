using System.Diagnostics;
using System.Net.Sockets;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Device.Services;

/// <summary>Manual advanced diagnostics — port scan with explicit user consent.</summary>
public sealed class AdvancedNetworkDiagnosticsService : IAdvancedNetworkDiagnosticsService
{
    private readonly ILogger _logger;

    public AdvancedNetworkDiagnosticsService(ILogger? logger = null) =>
        _logger = logger ?? Log.ForContext<AdvancedNetworkDiagnosticsService>();

    public async Task<IReadOnlyList<WirelessDiscoveryAttempt>> RunManualPortScanAsync(
        string ipAddress,
        int minPort = 37000,
        int maxPort = 47000,
        CancellationToken cancellationToken = default)
    {
        var attempts = new List<WirelessDiscoveryAttempt>();
        if (string.IsNullOrWhiteSpace(ipAddress))
            return attempts;

        minPort = Math.Clamp(minPort, 1, 65535);
        maxPort = Math.Clamp(maxPort, minPort, 65535);
        var range = maxPort - minPort;
        if (range > 2000)
            maxPort = minPort + 2000;

        _logger.Warning(
            "[AdvancedDiag] Manual port scan {Ip} {Min}-{Max} — IDS/AV risk",
            ipAddress, minPort, maxPort);

        using var gate = new SemaphoreSlim(8);
        var openPorts = new List<int>();

        var tasks = Enumerable.Range(minPort, maxPort - minPort + 1)
            .Select(async port =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (await IsOpenAsync(ipAddress, port, 200, cancellationToken).ConfigureAwait(false))
                        openPorts.Add(port);
                }
                finally
                {
                    gate.Release();
                }
            });

        var sw = Stopwatch.StartNew();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        sw.Stop();

        attempts.Add(new WirelessDiscoveryAttempt
        {
            Step = WirelessDiscoveryStep.PortScanManual,
            Success = openPorts.Count > 0,
            Duration = sw.Elapsed,
            Detail = openPorts.Count > 0
                ? $"open: {string.Join(", ", openPorts.Take(5))}"
                : "no open ports"
        });

        return attempts;
    }

    private static async Task<bool> IsOpenAsync(string ip, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeoutMs);
            await client.ConnectAsync(System.Net.IPAddress.Parse(ip), port, linked.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
