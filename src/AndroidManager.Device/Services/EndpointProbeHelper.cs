using System.Net;
using System.Net.Sockets;

namespace AndroidManager.Device.Services;

public static class EndpointProbeHelper
{
    public const int StaleEndpointTimeoutMs = 1500;

    public static async Task<bool> IsTcpOpenAsync(
        string ip,
        int port,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ip) || port is < 1 or > 65535)
            return false;

        try
        {
            using var client = new TcpClient();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeoutMs);
            await client.ConnectAsync(IPAddress.Parse(ip), port, linked.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
