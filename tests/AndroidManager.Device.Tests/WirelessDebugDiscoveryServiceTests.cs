using AndroidManager.Core.Models;
using AndroidManager.Device.Services;

namespace AndroidManager.Device.Tests;

public class WirelessDebugDiscoveryServiceTests
{
    [Fact]
    public void ParseMdnsOutput_ReadsTlsAndLegacyServices()
    {
        const string output = """
            List of discovered mdns services
            adb-Q9WT4JF37U	_adb._tcp	192.168.1.129:5555
            adb-941AY0HXWG-r8Nucn	_adb-tls-connect._tcp	192.168.0.147:37387
            adb-941AY0HXWG-TnSdi9	_adb-tls-pairing._tcp	192.168.0.147:42885
            """;

        var endpoints = WirelessDebugDiscoveryService.ParseMdnsOutput(output);

        Assert.Equal(3, endpoints.Count);
        Assert.Contains(endpoints, e => e.Kind == MdnsServiceKind.LegacyAdb && e.Port == 5555);
        Assert.Contains(endpoints, e => e.Kind == MdnsServiceKind.TlsConnect && e.Port == 37387);
        Assert.Contains(endpoints, e => e.Kind == MdnsServiceKind.TlsPairing && e.Port == 42885);
    }

    [Fact]
    public void ParseMdnsOutput_ReadsTrailingDotsAndExtraColumns()
    {
        const string output = """
            List of discovered mdns services
            adb-505df5ca	_adb._tcp.	192.168.1.127:5555
            adb-505df5ca-r8Nucn	_adb-tls-connect._tcp.	192.168.1.127:38129	wlan0
            adb-Q9WT4JF37U	_adb._tcp.local.	192.168.1.131:5555
            """;

        var endpoints = WirelessDebugDiscoveryService.ParseMdnsOutput(output);

        Assert.Equal(3, endpoints.Count);
        Assert.Contains(endpoints, e => e.Kind == MdnsServiceKind.LegacyAdb && e.IpAddress == "192.168.1.127" && e.Port == 5555);
        Assert.Contains(endpoints, e => e.Kind == MdnsServiceKind.TlsConnect && e.Port == 38129);
        Assert.Contains(endpoints, e => e.Kind == MdnsServiceKind.LegacyAdb && e.IpAddress == "192.168.1.131");
    }

    [Fact]
    public void GroupDevices_MergesPairingAndConnectByIp()
    {
        var endpoints = new List<MdnsAdbEndpoint>
        {
            new()
            {
                InstanceName = "adb-AAA-connect",
                ServiceType = "_adb-tls-connect._tcp",
                Kind = MdnsServiceKind.TlsConnect,
                IpAddress = "10.0.0.5",
                Port = 37111
            },
            new()
            {
                InstanceName = "adb-AAA-pair",
                ServiceType = "_adb-tls-pairing._tcp",
                Kind = MdnsServiceKind.TlsPairing,
                IpAddress = "10.0.0.5",
                Port = 42000
            }
        };

        var devices = WirelessDebugDiscoveryService.GroupDevices(endpoints);
        Assert.Single(devices);
        Assert.Equal(37111, devices[0].ConnectPort);
        Assert.Equal(42000, devices[0].PairingPort);
        Assert.Equal("10.0.0.5", devices[0].IpAddress);
    }
}
