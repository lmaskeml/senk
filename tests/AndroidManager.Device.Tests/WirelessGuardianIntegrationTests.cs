using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Device.Services;
using NSubstitute;

namespace AndroidManager.Device.Tests;

/// <summary>
/// Mock + local TcpListener ile Guardian keşif / presence / orchestrator entegrasyonu.
/// </summary>
public sealed class WirelessGuardianIntegrationTests : IDisposable
{
    private readonly TcpListener _listener;
    private readonly int _openPort;

    public WirelessGuardianIntegrationTests()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _openPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { /* ignore */ }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                client.Dispose();
            }
        }
        catch
        {
            // listener stopped
        }
    }

    [Fact]
    public async Task PresenceStore_CompanionIpChange_UpsertsNewEndpoint()
    {
        var platform = new MemoryPlatform();
        var store = new DevicePresenceStore(platform);

        store.RecordEndpoint("box-1", "192.168.1.15", 40000, "wifi");
        store.RecordCompanionHeartbeat(
            new CompanionDevice
            {
                DeviceId = "box-1",
                IpAddress = "192.168.1.42",
                WirelessAdbPort = 42137,
                NetworkType = "wifi",
                NetworkGeneration = 2,
                PortSource = "tls"
            },
            "box-1");

        var presence = store.Get("box-1");
        Assert.NotNull(presence);
        Assert.Equal("192.168.1.42", presence!.LastKnownIp);
        Assert.Equal(42137, presence.LastKnownPort);
        Assert.Equal(2, presence.LastNetworkGeneration);
        Assert.Equal("tls", presence.LastPortSource);

        // Persist fire-and-forget — kısa bekle
        await Task.Delay(80);
        var db = await platform.GetDevicePresenceAsync("box-1");
        Assert.NotNull(db);
        Assert.Equal("192.168.1.42", db!.LastKnownIp);
    }

    [Fact]
    public async Task StaleCachedEndpoint_InvalidatesPresence_WithinBudget()
    {
        var presence = new RecordingPresenceStore();
        presence.RecordEndpoint("stale-device", "127.0.0.1", 1, "wifi"); // port 1 closed

        var chain = CreateChain(
            mdnsDevices: [],
            companionDevices: [],
            presence,
            apiLevel: 33,
            mdnsEnabled: false,
            companionEnabled: false);

        var sw = Stopwatch.StartNew();
        var result = await chain.ResolveWiFiAsync(new WirelessWatchTarget
        {
            StableId = "stale-device",
            IpAddress = "127.0.0.1",
            Port = 1
        });
        sw.Stop();

        Assert.Null(result);
        Assert.Contains("stale-device", presence.Invalidated);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8),
            $"Stale probe took too long: {sw.Elapsed}");
    }

    [Fact]
    public async Task CompanionDiscovery_UsesLiveTlsPort_WhenOpen()
    {
        var presence = new RecordingPresenceStore();
        var companion = new CompanionDevice
        {
            DeviceId = "dev-live",
            IpAddress = "127.0.0.1",
            WirelessAdbPort = _openPort,
            WirelessDebugEnabled = true,
            DeviceName = "TestBox"
        };

        var chain = CreateChain(
            mdnsDevices: [],
            companionDevices: [companion],
            presence,
            apiLevel: 33,
            mdnsEnabled: false,
            companionEnabled: true);

        var result = await chain.ResolveWiFiAsync(new WirelessWatchTarget
        {
            StableId = "stable-live",
            DeviceId = "dev-live",
            IpAddress = "127.0.0.1",
            Port = 9999 // eski / yanlış port
        });

        Assert.NotNull(result);
        Assert.Equal("127.0.0.1", result!.IpAddress);
        Assert.Equal(_openPort, result.Port);
        Assert.Equal(EndpointDiscoverySource.Companion.ToString(), result.Source);
    }

    [Fact]
    public async Task MdnsDiscovery_FindsPort_WhenCompanionDisabled()
    {
        var presence = new RecordingPresenceStore();
        var mdns = new WirelessDebugDevice
        {
            DeviceId = "mdns-dev",
            DisplayName = "MDNS Box",
            IpAddress = "127.0.0.1",
            ConnectPort = _openPort
        };

        var chain = CreateChain(
            mdnsDevices: [mdns],
            companionDevices: [],
            presence,
            apiLevel: 33,
            mdnsEnabled: true,
            companionEnabled: false);

        var result = await chain.ResolveWiFiAsync(new WirelessWatchTarget
        {
            StableId = "stable-mdns",
            DeviceId = "mdns-dev",
            IpAddress = "10.0.0.99",
            Port = 5555
        });

        Assert.NotNull(result);
        Assert.Equal(_openPort, result!.Port);
        Assert.Equal(EndpointDiscoverySource.Mdns.ToString(), result.Source);
    }

    [Fact]
    public async Task LegacyTcp_Skipped_OnAndroid11Plus()
    {
        var presence = new RecordingPresenceStore();
        var chain = CreateChain(
            mdnsDevices: [],
            companionDevices: [],
            presence,
            apiLevel: 33,
            mdnsEnabled: false,
            companionEnabled: false);

        // Cache boş (port 1 kapalı) → legacy'ye düşer ama API 33 → atlanır
        var result = await chain.ResolveWiFiAsync(new WirelessWatchTarget
        {
            StableId = "legacy-skip",
            IpAddress = "127.0.0.1",
            Port = 1
        });

        Assert.Null(result);
        Assert.DoesNotContain(chain.LastAttempts, a => a.Step == WirelessDiscoveryStep.LegacyTcp && a.Success);
    }

    [Fact]
    public async Task Orchestrator_UsbRootWins_WhenWifiChainEmpty()
    {
        var chain = Substitute.For<IWirelessDiscoveryChain>();
        chain.ResolveWiFiAsync(Arg.Any<WirelessWatchTarget>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await Task.Delay(200);
                return null;
            });

        var usb = Substitute.For<IUsbConnectionMonitor>();
        usb.FindUsbLinkAsync(Arg.Any<WirelessWatchTarget>(), Arg.Any<CancellationToken>())
            .Returns(new UsbDeviceLink { Serial = "USB1", StableId = "s1", IsOnline = true });

        var root = Substitute.For<IRootWirelessDebugService>();
        root.IsRootAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        root.ProbeAsync(Arg.Any<CancellationToken>()).Returns(new RootWirelessDebugInfo
        {
            RootAvailable = true,
            TlsPort = _openPort,
            WifiIp = "127.0.0.1",
            WirelessDebugEnabled = true
        });

        var presence = new RecordingPresenceStore();
        var orchestrator = new WirelessConnectionOrchestrator(chain, usb, root, presence);

        var ep = await orchestrator.ResolveAsync(new WirelessWatchTarget
        {
            StableId = "s1",
            IpAddress = "127.0.0.1",
            Port = 1
        });

        Assert.NotNull(ep);
        Assert.Equal(_openPort, ep!.Port);
        Assert.Equal(EndpointDiscoverySource.Root.ToString(), ep.Source);
        Assert.Equal("127.0.0.1", presence.Get("s1")?.LastKnownIp);
    }

    [Fact]
    public async Task Orchestrator_WifiWins_WhenFasterThanUsb()
    {
        var wifiEp = new WirelessEndpoint
        {
            IpAddress = "127.0.0.1",
            Port = _openPort,
            Source = EndpointDiscoverySource.Companion.ToString(),
            WirelessDebugLikelyEnabled = true
        };

        var chain = Substitute.For<IWirelessDiscoveryChain>();
        chain.ResolveWiFiAsync(Arg.Any<WirelessWatchTarget>(), Arg.Any<CancellationToken>())
            .Returns(wifiEp);

        var usb = Substitute.For<IUsbConnectionMonitor>();
        usb.FindUsbLinkAsync(Arg.Any<WirelessWatchTarget>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.Run(async () =>
            {
                await Task.Delay(500);
                return (UsbDeviceLink?)new UsbDeviceLink { Serial = "USB1", StableId = "s2", IsOnline = true };
            }));

        var root = Substitute.For<IRootWirelessDebugService>();
        root.IsRootAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);

        var orchestrator = new WirelessConnectionOrchestrator(
            chain, usb, root, new RecordingPresenceStore());

        var sw = Stopwatch.StartNew();
        var ep = await orchestrator.ResolveAsync(new WirelessWatchTarget
        {
            StableId = "s2",
            IpAddress = "127.0.0.1",
            Port = _openPort
        });
        sw.Stop();

        Assert.NotNull(ep);
        Assert.Equal(EndpointDiscoverySource.Companion.ToString(), ep!.Source);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(400),
            $"WiFi should win quickly, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task EndpointProbe_OpenPort_Succeeds_ClosedPort_FailsFast()
    {
        Assert.True(await EndpointProbeHelper.IsTcpOpenAsync(
            "127.0.0.1", _openPort, EndpointProbeHelper.StaleEndpointTimeoutMs));

        var sw = Stopwatch.StartNew();
        Assert.False(await EndpointProbeHelper.IsTcpOpenAsync(
            "127.0.0.1", 1, EndpointProbeHelper.StaleEndpointTimeoutMs));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
    }

    private static WirelessDiscoveryChain CreateChain(
        IReadOnlyList<WirelessDebugDevice> mdnsDevices,
        IReadOnlyList<CompanionDevice> companionDevices,
        IDevicePresenceStore presence,
        int apiLevel,
        bool mdnsEnabled,
        bool companionEnabled)
    {
        var wireless = Substitute.For<IWirelessDebugDiscoveryService>();
        wireless.GetDiscoveredDevicesAsync(Arg.Any<CancellationToken>())
            .Returns(mdnsDevices);

        var companion = Substitute.For<ICompanionDiscoveryService>();
        companion.GetCachedDevices().Returns(companionDevices);

        var identity = new DeviceIdentityService();
        var lanProbe = new LanProbeStrategy(companion, identity);

        var root = Substitute.For<IRootWirelessDebugService>();
        root.IsRootAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);
        root.GetDeviceApiLevelAsync(Arg.Any<CancellationToken>()).Returns(apiLevel);
        root.ProbeAsync(Arg.Any<CancellationToken>()).Returns(new RootWirelessDebugInfo());

        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings
        {
            WirelessMdnsDiscovery = mdnsEnabled,
            WirelessCompanionDiscovery = companionEnabled,
            WirelessAutoReconnect = true
        });

        return new WirelessDiscoveryChain(
            wireless, companion, lanProbe, root, identity, settings, presence);
    }

    private sealed class RecordingPresenceStore : IDevicePresenceStore
    {
        private readonly Dictionary<string, DevicePresenceRecord> _map = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Invalidated { get; } = [];

        public void RecordCompanionHeartbeat(CompanionDevice device, string stableId)
        {
            RecordEndpoint(stableId, device.IpAddress, device.EffectiveAdbPort, device.NetworkType);
            if (_map.TryGetValue(stableId, out var r))
            {
                r.LastCompanionOnline = true;
                r.LastNetworkGeneration = device.NetworkGeneration;
                r.LastPortSource = device.PortSource;
            }
        }

        public void RecordEndpoint(string stableId, string ip, int port, string? networkType = null)
        {
            _map[stableId] = new DevicePresenceRecord
            {
                StableId = stableId,
                LastSeen = DateTime.Now,
                LastKnownIp = ip,
                LastKnownPort = port,
                LastNetworkType = networkType
            };
        }

        public DevicePresenceRecord? Get(string stableId) =>
            _map.TryGetValue(stableId, out var r) ? r : null;

        public void InvalidateEndpoint(string stableId)
        {
            Invalidated.Add(stableId);
            if (_map.TryGetValue(stableId, out var r))
            {
                r.LastKnownIp = null;
                r.LastKnownPort = null;
                r.LastCompanionOnline = false;
            }
        }
    }

    private sealed class MemoryPlatform : IPlatformRepository
    {
        private readonly Dictionary<string, DevicePresenceRecord> _presence = new(StringComparer.OrdinalIgnoreCase);

        public Task<int> SavePartitionBackupAsync(PartitionBackupRecord record, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<PartitionBackupRecord>> ListPartitionBackupsAsync(
            string? stableDeviceId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PartitionBackupRecord>>([]);

        public Task SaveScanHistoryAsync(ScanHistoryEntry entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ScanHistoryEntry>> ListScanHistoryAsync(
            int limit = 30, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ScanHistoryEntry>>([]);

        public Task UpsertQuarantineItemAsync(ThreatItem threat, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveQuarantineItemAsync(string threatId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ThreatItem>> ListQuarantineItemsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ThreatItem>>([]);

        public Task UpsertDevicePresenceAsync(DevicePresenceRecord record, CancellationToken cancellationToken = default)
        {
            _presence[record.StableId] = record;
            return Task.CompletedTask;
        }

        public Task<DevicePresenceRecord?> GetDevicePresenceAsync(string stableId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_presence.TryGetValue(stableId, out var r) ? r : null);

        public Task InvalidateDevicePresenceEndpointAsync(string stableId, CancellationToken cancellationToken = default)
        {
            if (_presence.TryGetValue(stableId, out var r))
            {
                r.LastKnownIp = null;
                r.LastKnownPort = null;
                r.LastCompanionOnline = false;
            }

            return Task.CompletedTask;
        }
    }
}
