using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Device.Services;
using NSubstitute;
using Xunit;

namespace AndroidManager.Device.Tests;

public sealed class MultiDeviceGuardianTests
{
    [Fact]
    public void Start_TwoDevices_CreatesIndependentSessions()
    {
        var watchdog = CreateWatchdog();

        watchdog.Start(new WirelessWatchTarget
        {
            IpAddress = "192.168.1.10",
            Port = 37123,
            StableId = "device-a",
            DeviceName = "Phone A",
            AutoReconnect = true
        });

        watchdog.Start(new WirelessWatchTarget
        {
            IpAddress = "192.168.1.20",
            Port = 42187,
            StableId = "device-b",
            DeviceName = "Phone B",
            AutoReconnect = true
        });

        Assert.True(watchdog.IsWatching);
        Assert.Equal(2, watchdog.WatchedStableIds.Count);
        Assert.True(watchdog.IsWatchingDevice("device-a"));
        Assert.True(watchdog.IsWatchingDevice("device-b"));

        watchdog.StopDevice("device-a");
        Assert.False(watchdog.IsWatchingDevice("device-a"));
        Assert.True(watchdog.IsWatchingDevice("device-b"));
        Assert.True(watchdog.IsWatching);

        watchdog.Stop();
        Assert.False(watchdog.IsWatching);
        Assert.Empty(watchdog.WatchedStableIds);

        watchdog.Dispose();
    }

    [Fact]
    public void Start_SameStableId_ReplacesSessionNotDuplicate()
    {
        var watchdog = CreateWatchdog();

        watchdog.Start(new WirelessWatchTarget
        {
            IpAddress = "192.168.1.10",
            Port = 11111,
            StableId = "same-device",
            AutoReconnect = true
        });
        watchdog.Start(new WirelessWatchTarget
        {
            IpAddress = "192.168.1.10",
            Port = 22222,
            StableId = "same-device",
            AutoReconnect = true
        });

        Assert.Single(watchdog.WatchedStableIds);
        Assert.Equal("192.168.1.10:22222", watchdog.WatchedEndpoint);

        watchdog.Dispose();
    }

    [Fact]
    public void RequestReconnect_WhileWatching_DoesNotThrow()
    {
        var watchdog = CreateWatchdog();
        watchdog.Start(new WirelessWatchTarget
        {
            IpAddress = "192.168.1.50",
            Port = 37123,
            StableId = "device-reconnect",
            AutoReconnect = true
        });

        watchdog.RequestReconnect();
        watchdog.RequestReconnect("device-reconnect");

        Assert.True(watchdog.IsWatchingDevice("device-reconnect"));
        watchdog.Dispose();
    }

    private static ConnectionWatchdog CreateWatchdog()
    {
        var adb = Substitute.For<IAdbService>();
        adb.GetDevicesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ConnectedDevice>>([]));

        var orchestrator = Substitute.For<IWirelessConnectionOrchestrator>();
        var recovery = Substitute.For<IWirelessDebugRecoveryService>();
        var presenceStore = Substitute.For<IDevicePresenceStore>();
        var companion = Substitute.For<ICompanionDiscoveryService>();
        var wireless = Substitute.For<IWirelessDebugDiscoveryService>();
        var persist = Substitute.For<IConnectionPersistService>();
        var identity = Substitute.For<IDeviceIdentityService>();
        identity.ComputeStableId(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(ci => $"id:{ci.ArgAt<string?>(0) ?? ci.ArgAt<string?>(1) ?? "x"}");

        var presence = Substitute.For<IDevicePresenceService>();
        var pairing = Substitute.For<IPairingCredentialStore>();
        var batteryGuard = Substitute.For<ICompanionBatteryGuard>();
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings
        {
            WirelessAutoReconnect = true,
            WirelessCompanionDiscovery = false,
            WirelessMdnsDiscovery = false
        });

        return new ConnectionWatchdog(
            adb, orchestrator, recovery, presenceStore, companion, wireless, persist, identity, presence, pairing, settings, batteryGuard);
    }
}
