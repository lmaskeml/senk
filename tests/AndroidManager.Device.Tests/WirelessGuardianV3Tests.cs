using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Device.Services;
using NSubstitute;
using Xunit;

namespace AndroidManager.Device.Tests;

public sealed class LanProbeCandidateTests
{
    [Fact]
    public void BuildCandidateList_IncludesLastKnownAndNeighbors()
    {
        var strategy = new LanProbeStrategy(
            new FakeCompanionDiscovery(),
            new DeviceIdentityService());

        var method = typeof(LanProbeStrategy).GetMethod(
            "BuildCandidateList",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var list = (List<string>)method!.Invoke(strategy, ["192.168.1.42"])!;
        Assert.Contains("192.168.1.42", list);
        Assert.Contains("192.168.1.41", list);
        Assert.Contains("192.168.1.43", list);
    }

    private sealed class FakeCompanionDiscovery : ICompanionDiscoveryService
    {
        public event EventHandler<CompanionDevice>? DeviceDiscovered
        {
            add { }
            remove { }
        }

        public IReadOnlyList<CompanionDevice> GetCachedDevices() => [];

        public Task<CompanionConnectResult> ConnectWithPairCodeAsync(
            string ip, int tcpPort, string pairCode, string pcName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CompanionConnectResult());

        public Task<CompanionConnectResult> ConnectFromQrAsync(
            string qrContent, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CompanionConnectResult());

        public Task StartListeningAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void StopListening()
        {
        }

        public void Dispose()
        {
        }
    }
}

public sealed class DevicePresenceRecordTests
{
    [Fact]
    public void LastSeenDisplay_FormatsMinutes()
    {
        var record = new DevicePresenceRecord
        {
            LastSeen = DateTime.Now.AddMinutes(-5)
        };
        Assert.Contains("dakika", record.LastSeenDisplay);
    }

    [Fact]
    public void IsStale_ReturnsTrueWhenOlderThanThreshold()
    {
        var record = new DevicePresenceRecord
        {
            LastSeen = DateTime.Now.AddMinutes(-10)
        };
        Assert.True(record.IsStale(TimeSpan.FromMinutes(3)));
    }
}

public sealed class WirelessGuardianV3Tests
{
    [Fact]
    public void EndpointDiscoverySource_RootIsDistinct()
    {
        Assert.Equal("Root", EndpointDiscoverySource.Root.ToString());
        Assert.Equal("LanProbe", EndpointDiscoverySource.LanProbe.ToString());
    }

    [Fact]
    public void WdEnableResult_HasNotSupportedForAndroid11Plus()
    {
        Assert.Equal(0, (int)WdEnableResult.NotSupported);
        Assert.Contains(WdEnableResult.NotSupported, Enum.GetValues<WdEnableResult>());
        Assert.Contains(WdEnableResult.MaybeEnabled, Enum.GetValues<WdEnableResult>());
    }

    [Fact]
    public void WirelessDiagnosticScenario_CoversGuardianCases()
    {
        Assert.Contains(WirelessDiagnosticScenario.PortChanged, Enum.GetValues<WirelessDiagnosticScenario>());
        Assert.Contains(WirelessDiagnosticScenario.WirelessDebugOff, Enum.GetValues<WirelessDiagnosticScenario>());
        Assert.Contains(WirelessDiagnosticScenario.NetworkChanged, Enum.GetValues<WirelessDiagnosticScenario>());
        Assert.Contains(WirelessDiagnosticScenario.DeviceOnCellular, Enum.GetValues<WirelessDiagnosticScenario>());
    }

    [Fact]
    public void Orchestrator_InferScenario_CellularWhenLastNetworkTypeCellular()
    {
        var orchestrator = new WirelessConnectionOrchestrator(
            new FakeDiscoveryChain(),
            new FakeUsbMonitor(),
            new FakeRootWireless(),
            new FakePresenceStore());

        var presence = new DevicePresenceRecord
        {
            LastSeen = DateTime.Now,
            LastNetworkType = "cellular"
        };

        var scenario = orchestrator.InferScenario(new WirelessWatchTarget { StableId = "s1" }, presence);
        Assert.Equal(WirelessDiagnosticScenario.DeviceOnCellular, scenario);
    }

    [Fact]
    public void Orchestrator_InferScenario_WirelessDebugOffWhenCompanionRecent()
    {
        var orchestrator = new WirelessConnectionOrchestrator(
            new FakeDiscoveryChain(),
            new FakeUsbMonitor(),
            new FakeRootWireless(),
            new FakePresenceStore());

        var presence = new DevicePresenceRecord
        {
            LastSeen = DateTime.Now,
            LastCompanionOnline = true,
            LastKnownPort = 40000
        };

        var scenario = orchestrator.InferScenario(new WirelessWatchTarget { StableId = "s1" }, presence);
        Assert.Equal(WirelessDiagnosticScenario.WirelessDebugOff, scenario);
    }

    [Fact]
    public void Orchestrator_InferScenario_DeviceOfflineWhenStale()
    {
        var orchestrator = new WirelessConnectionOrchestrator(
            new FakeDiscoveryChain(),
            new FakeUsbMonitor(),
            new FakeRootWireless(),
            new FakePresenceStore());

        var presence = new DevicePresenceRecord
        {
            LastSeen = DateTime.Now.AddHours(-2)
        };

        var scenario = orchestrator.InferScenario(new WirelessWatchTarget { StableId = "s1" }, presence);
        Assert.Equal(WirelessDiagnosticScenario.DeviceOffline, scenario);
    }

    [Fact]
    public void DevicePresenceStore_PersistsViaPlatformRepository()
    {
        var platform = new InMemoryPlatformRepository();
        var store = new DevicePresenceStore(platform);

        store.RecordEndpoint("stable-1", "192.168.1.10", 42137, "wifi");
        var loaded = store.Get("stable-1");

        Assert.NotNull(loaded);
        Assert.Equal("192.168.1.10", loaded!.LastKnownIp);
        Assert.Equal(42137, loaded.LastKnownPort);
        Assert.Equal("wifi", loaded.LastNetworkType);
    }

    private sealed class FakeDiscoveryChain : IWirelessDiscoveryChain
    {
        public IReadOnlyList<WirelessDiscoveryAttempt> LastAttempts => [];

        public Task<WirelessEndpoint?> ResolveWiFiAsync(
            WirelessWatchTarget target,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<WirelessEndpoint?>(null);
    }

    private sealed class FakeUsbMonitor : IUsbConnectionMonitor
    {
        public Task<UsbDeviceLink?> FindUsbLinkAsync(
            WirelessWatchTarget target,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<UsbDeviceLink?>(null);

        public Task<bool> IsUsbOnlineForTargetAsync(
            WirelessWatchTarget target,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class FakeRootWireless : IRootWirelessDebugService
    {
        public Task<bool> IsRootAvailableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<RootWirelessDebugInfo> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RootWirelessDebugInfo());

        public Task<int?> GetTlsPortAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(null);

        public Task<string?> GetWifiIpAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<WdEnableResult> TryEnableWirelessDebugAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(WdEnableResult.NotSupported);

        public Task<int?> GetDeviceApiLevelAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(33);

        public Task<bool> TryEnableLegacyTcpAdbAsync(int port = 5555, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class FakePresenceStore : IDevicePresenceStore
    {
        public void RecordCompanionHeartbeat(CompanionDevice device, string stableId)
        {
        }

        public void RecordEndpoint(string stableId, string ip, int port, string? networkType = null)
        {
        }

        public DevicePresenceRecord? Get(string stableId) => null;

        public void InvalidateEndpoint(string stableId)
        {
        }
    }

    private sealed class InMemoryPlatformRepository : IPlatformRepository
    {
        private readonly Dictionary<string, DevicePresenceRecord> _presence =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<int> SavePartitionBackupAsync(PartitionBackupRecord record, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<PartitionBackupRecord>> ListPartitionBackupsAsync(
            string? stableDeviceId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PartitionBackupRecord>>([]);

        public Task SaveScanHistoryAsync(ScanHistoryEntry entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ScanHistoryEntry>> ListScanHistoryAsync(
            int limit = 30,
            CancellationToken cancellationToken = default) =>
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

        public Task<DevicePresenceRecord?> GetDevicePresenceAsync(
            string stableId,
            CancellationToken cancellationToken = default) =>
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

public sealed class CompanionBatteryGuardTests
{
    [Fact]
    public async Task TryEnsureCompanion_SendsDeviceIdleWhitelistAndAppOps()
    {
        var adb = Substitute.For<IAdbService>();
        adb.SelectedDevice.Returns(new ConnectedDevice { Serial = "ABC123", State = "device" });
        adb.RunHostAdbAsync(
                Arg.Any<string?>(),
                Arg.Any<string>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>())
            .Returns((0, "added"));

        var guard = new CompanionBatteryGuardService(adb);
        var ok = await guard.TryEnsureCompanionCanRunInBackgroundAsync("ABC123");

        Assert.True(ok);
        await adb.Received().RunHostAdbAsync(
            "ABC123",
            Arg.Is<string>(s => s.Contains("deviceidle whitelist")),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
        await adb.Received().RunHostAdbAsync(
            "ABC123",
            Arg.Is<string>(s => s.Contains("RUN_IN_BACKGROUND")),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void StaleEndpointTimeout_IsOnePointFiveSeconds()
    {
        Assert.Equal(1500, EndpointProbeHelper.StaleEndpointTimeoutMs);
    }
}
