using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Events;
using AndroidManager.Core.Models;
using AndroidManager.Security.Data;
using AndroidManager.Security.Engines;
using AndroidManager.Security.Services;
using Xunit;

namespace AndroidManager.Security.Tests;

public sealed class ThreatDatabaseTests
{
    private readonly ThreatDatabase _db = CreateDb();

    private static ThreatDatabase CreateDb()
    {
        var db = new ThreatDatabase();
        db.Load();
        return db;
    }

    [Fact]
    public void CheckPackage_KnownMalware_ReturnsCritical()
    {
        var hit = _db.CheckPackage("com.imagecompress.android");
        Assert.NotNull(hit);
        Assert.Equal(ThreatSeverity.Critical, hit!.Severity);
        Assert.Contains("Joker", hit.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CheckPackage_Unknown_ReturnsNull() =>
        Assert.Null(_db.CheckPackage("com.legitimate.calculator"));

    [Fact]
    public void CheckHash_NullOrGarbage_NoMatch()
    {
        Assert.Null(_db.CheckHash(null, null));
        Assert.Null(_db.CheckHash("", ""));
        Assert.Null(_db.CheckHash(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
    }

    [Fact]
    public void DangerousCombination_SmsFraud_Detected()
    {
        var combos = _db.CheckDangerousCombinations(
        [
            "android.permission.SEND_SMS",
            "android.permission.RECEIVE_SMS",
            "android.permission.INTERNET"
        ]);
        Assert.Contains(combos, c => c.Name.Contains("SMS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnalyzePermissions_InternetAlone_NotDangerousListHit() =>
        Assert.Empty(_db.AnalyzePermissions(["android.permission.INTERNET"]));

    [Fact]
    public void IsKnownC2_DetectsListedHost()
    {
        Assert.True(_db.IsKnownC2("https://cerberus-android.pw/panel"));
        Assert.False(_db.IsKnownC2("https://google.com"));
    }
}

public sealed class RiskEngineFalsePositiveTests
{
    [Fact]
    public void SoFile_ExtensionAlone_BelowThreatThreshold()
    {
        var item = RiskEngine.Finalize(new ThreatItem
        {
            Name = "libnative.so",
            FilePath = "/system/lib64/libnative.so",
            Type = ThreatType.Suspicious,
            Evidence = [new ThreatEvidence { Description = "Native library (.so)", ScoreWeight = 5 }]
        });
        Assert.True(item.RiskScore < RiskEngine.ThreatThreshold);
        Assert.False(RiskEngine.MeetsThreatThreshold(item));
    }

    [Fact]
    public void DexFile_ExtensionAlone_BelowThreatThreshold()
    {
        var item = RiskEngine.Finalize(new ThreatItem
        {
            Name = "classes.dex",
            FilePath = "/data/app/com.example/classes.dex",
            Type = ThreatType.Suspicious,
            Evidence = [new ThreatEvidence { Description = "Dalvik executable (.dex)", ScoreWeight = 5 }]
        });
        Assert.True(item.RiskScore < RiskEngine.ThreatThreshold);
        Assert.False(RiskEngine.MeetsThreatThreshold(item));
    }

    [Fact]
    public void LegitimateApk_LowEvidence_NotThreat()
    {
        var item = RiskEngine.Finalize(new ThreatItem
        {
            Name = "Chrome",
            PackageName = "com.android.chrome",
            Type = ThreatType.Suspicious,
            Evidence =
            [
                new ThreatEvidence { Description = "APK found", ScoreWeight = 3 },
                new ThreatEvidence { Description = "INTERNET permission", ScoreWeight = 2 }
            ]
        });
        Assert.False(RiskEngine.MeetsThreatThreshold(item));
    }

    [Fact]
    public void SuspiciousPermissionCombo_MeetsThreshold()
    {
        var item = RiskEngine.Finalize(new ThreatItem
        {
            Name = "Spy Candidate",
            PackageName = "com.evil.spy",
            Type = ThreatType.Spyware,
            Evidence =
            [
                new ThreatEvidence { Description = "Accessibility", ScoreWeight = 20 },
                new ThreatEvidence { Description = "READ_SMS", ScoreWeight = 15 },
                new ThreatEvidence { Description = "INTERNET", ScoreWeight = 10 }
            ]
        });
        Assert.True(item.RiskScore >= RiskEngine.ThreatThreshold);
        Assert.True(RiskEngine.MeetsThreatThreshold(item));
    }

    [Fact]
    public void KnownMalwareType_MeetsThresholdEvenWithLowScore()
    {
        var item = RiskEngine.Finalize(new ThreatItem
        {
            Name = "TrojanX",
            Type = ThreatType.Trojan,
            RiskScore = 5,
            Evidence = []
        });
        Assert.True(RiskEngine.MeetsThreatThreshold(item));
    }

    [Fact]
    public void BuildScoreCard_CleanDevice_HighScore() =>
        Assert.Equal(100, RiskEngine.BuildScoreCard([], []).Score);

    [Fact]
    public void BuildScoreCard_CriticalThreat_LowersScore()
    {
        var card = RiskEngine.BuildScoreCard(
            [new ThreatItem { Name = "Bad", Severity = ThreatSeverity.Critical }],
            []);
        Assert.True(card.Score <= 75);
    }
}

public sealed class FastbootDeviceProbeTests
{
    [Fact]
    public void ParseDevices_ExtractsSerials()
    {
        var devices = FastbootDeviceProbe.ParseDevices("abc123\tfastboot\ndef456 fastboot\n");
        Assert.Equal(2, devices.Count);
        Assert.Contains("abc123", devices);
        Assert.Contains("def456", devices);
    }

    [Fact]
    public void ParseDevices_IgnoresNonFastbootLines() =>
        Assert.Empty(FastbootDeviceProbe.ParseDevices("serial1\tdevice\n"));

    [Fact]
    public void ParseDevices_Empty_ReturnsEmpty()
    {
        Assert.Empty(FastbootDeviceProbe.ParseDevices(""));
        Assert.Empty(FastbootDeviceProbe.ParseDevices("   "));
    }
}

public sealed class QuarantineIndexTests
{
    [Fact]
    public async Task Quarantine_PackageDisable_AddsToIndex()
    {
        var adb = new FakeAdbService();
        adb.Respond("pm disable-user --user 0 com.test.bad", "Package com.test.bad new state: disabled");
        adb.Respond("pm path com.test.bad", "package:/data/app/com.test.bad/base.apk");

        var platform = new FakePlatformRepository();
        var qm = new QuarantineManager(adb, new FakeSyncService(), platform);
        var threat = new ThreatItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Test Bad",
            PackageName = "com.test.bad",
            Type = ThreatType.Malware,
            Severity = ThreatSeverity.High
        };

        Assert.True(await qm.QuarantineAsync(threat, CancellationToken.None));
        Assert.True(threat.IsQuarantined);
        Assert.Contains(await qm.GetListAsync(), t => t.PackageName == "com.test.bad");
    }

    [Fact]
    public async Task Restore_WhenStillThreat_Blocked()
    {
        var qm = new QuarantineManager(new FakeAdbService(), new FakeSyncService(), new FakePlatformRepository());
        var threat = new ThreatItem
        {
            Id = "restore-test",
            Name = "Still Bad",
            PackageName = "com.test.still",
            Type = ThreatType.Trojan,
            IsQuarantined = true
        };

        Assert.False(await qm.RestoreAsync(threat, (_, _) => Task.FromResult(true), CancellationToken.None));
    }

    [Fact]
    public async Task Restore_WhenClean_EnablesPackage()
    {
        var adb = new FakeAdbService();
        adb.Respond("pm enable com.test.ok", "Package com.test.ok new state: enabled");
        var qm = new QuarantineManager(adb, new FakeSyncService(), new FakePlatformRepository());
        var threat = new ThreatItem
        {
            Id = "restore-ok",
            Name = "Was Bad",
            PackageName = "com.test.ok",
            Type = ThreatType.Adware,
            IsQuarantined = true
        };

        Assert.True(await qm.RestoreAsync(threat, (_, _) => Task.FromResult(false), CancellationToken.None));
    }
}

file sealed class FakePlatformRepository : IPlatformRepository
{
    private readonly List<ThreatItem> _quarantine = [];
    private readonly List<ScanHistoryEntry> _scans = [];

    public Task<int> SavePartitionBackupAsync(PartitionBackupRecord record, CancellationToken cancellationToken = default) =>
        Task.FromResult(1);

    public Task<IReadOnlyList<PartitionBackupRecord>> ListPartitionBackupsAsync(
        string? stableDeviceId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PartitionBackupRecord>>([]);

    public Task SaveScanHistoryAsync(ScanHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        _scans.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ScanHistoryEntry>> ListScanHistoryAsync(
        int limit = 30, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ScanHistoryEntry>>(_scans.Take(limit).ToList());

    public Task UpsertQuarantineItemAsync(ThreatItem threat, CancellationToken cancellationToken = default)
    {
        _quarantine.RemoveAll(t => t.Id == threat.Id || t.PackageName == threat.PackageName);
        _quarantine.Add(threat);
        return Task.CompletedTask;
    }

    public Task RemoveQuarantineItemAsync(string threatId, CancellationToken cancellationToken = default)
    {
        _quarantine.RemoveAll(t => t.Id == threatId || t.PackageName == threatId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ThreatItem>> ListQuarantineItemsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ThreatItem>>(_quarantine.ToList());

    public Task UpsertDevicePresenceAsync(DevicePresenceRecord record, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<DevicePresenceRecord?> GetDevicePresenceAsync(string stableId, CancellationToken cancellationToken = default) =>
        Task.FromResult<DevicePresenceRecord?>(null);

    public Task InvalidateDevicePresenceEndpointAsync(string stableId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

file sealed class FakeAdbService : IAdbService
{
    private readonly Dictionary<string, string> _responses = new(StringComparer.OrdinalIgnoreCase);

    public ConnectedDevice? SelectedDevice => null;
    public IReadOnlyList<ConnectedDevice> Devices => [];
    public bool IsRunning => true;

    public event EventHandler<DeviceConnectionChangedEventArgs>? DeviceConnectionChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<ConnectedDevice?>? SelectedDeviceChanged
    {
        add { }
        remove { }
    }

    public void Respond(string commandContains, string output) =>
        _responses[commandContains] = output;

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<ConnectedDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ConnectedDevice>>([]);
    public void SelectDevice(ConnectedDevice? device) { }

    public Task<string> ExecuteShellAsync(string command, CancellationToken cancellationToken = default)
    {
        foreach (var kv in _responses)
        {
            if (command.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(kv.Value);
        }

        return Task.FromResult("");
    }

    public async Task RunExclusiveAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default) =>
        await action(cancellationToken).ConfigureAwait(false);

    public Task<bool> ConnectWifiAsync(string ipAddress, int port = 5555, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
    public Task<bool> PairWifiAsync(string ipAddress, int port, string pairingCode, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
    public Task<InstallResult> InstallLocalPackagesAsync(IReadOnlyList<string> localApkPaths, CancellationToken cancellationToken = default) =>
        Task.FromResult(new InstallResult());
    public Task EnableTcpipAsync(int port = 5555, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<string?> GetDeviceIpAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    public Task<UsbWifiSwitchResult> SwitchUsbToWifiAsync(int port = 5555, CancellationToken cancellationToken = default) =>
        Task.FromResult(new UsbWifiSwitchResult());
    public Task<DeviceToolResult> SideloadAsync(
        string zipPath,
        IProgress<SideloadProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new DeviceToolResult { Success = false, Message = "not implemented" });
    public Task<BlockDumpResult> ExecOutToFileAsync(
        string remoteCommand,
        string localFilePath,
        long expectedBytes = 0,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new BlockDumpResult { Success = false, Message = "not implemented" });
    public Task<ConnectedDevice?> WaitForAdbDeviceAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ConnectedDevice?>(null);

    public Task<DeviceToolResult> RebootTransportAsync(
        string? target,
        string? serial = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new DeviceToolResult { Success = true, Message = target ?? "reboot" });

    public Task<(int ExitCode, string Output)> RunHostAdbAsync(
        string? serial,
        string arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult((0, ""));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

file sealed class FakeSyncService : IAdbSyncService
{
    public Task PullAsync(string remotePath, string localPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
    public Task PushAsync(string localPath, string remotePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
