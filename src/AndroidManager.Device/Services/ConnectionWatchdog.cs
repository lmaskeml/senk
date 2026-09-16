using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using AndroidManager.Core;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Device.Services;

/// <summary>
/// Wireless Connection Guardian hub — one independent session per StableId.
/// </summary>
public sealed class ConnectionWatchdog : IConnectionWatchdog
{
    private readonly ConcurrentDictionary<string, DeviceGuardianSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly IAdbService _adb;
    private readonly IWirelessConnectionOrchestrator _orchestrator;
    private readonly IWirelessDebugRecoveryService _recovery;
    private readonly IDevicePresenceStore _presenceStore;
    private readonly ICompanionDiscoveryService _companion;
    private readonly IWirelessDebugDiscoveryService _wireless;
    private readonly IConnectionPersistService _persist;
    private readonly IDeviceIdentityService _identity;
    private readonly IDevicePresenceService _presence;
    private readonly IPairingCredentialStore _pairingStore;
    private readonly ISettingsService _settings;
    private readonly ICompanionBatteryGuard _batteryGuard;
    private readonly ILogger _logger;

    private int _discoveryRefCount;
    private bool _disposed;
    private string? _primaryStableId;
    private WirelessLinkStatus _status = new() { State = WirelessLinkState.Idle, Message = "İzlenmiyor" };

    public bool IsWatching => !_sessions.IsEmpty;
    public string? WatchedEndpoint =>
        _primaryStableId is not null
        && _sessions.TryGetValue(_primaryStableId, out var s)
            ? s.WatchedEndpoint
            : _sessions.Values.FirstOrDefault()?.WatchedEndpoint;

    public WirelessLinkStatus CurrentStatus => _status;
    public IReadOnlyCollection<string> WatchedStableIds => _sessions.Keys.ToArray();

    public event EventHandler? ConnectionLost;
    public event EventHandler<string>? ConnectionRestored;
    public event EventHandler<WirelessLinkStatus>? StatusChanged;

    public ConnectionWatchdog(
        IAdbService adb,
        IWirelessConnectionOrchestrator orchestrator,
        IWirelessDebugRecoveryService recovery,
        IDevicePresenceStore presenceStore,
        ICompanionDiscoveryService companion,
        IWirelessDebugDiscoveryService wireless,
        IConnectionPersistService persist,
        IDeviceIdentityService identity,
        IDevicePresenceService presence,
        IPairingCredentialStore pairingStore,
        ISettingsService settings,
        ICompanionBatteryGuard batteryGuard,
        ILogger? logger = null)
    {
        _adb = adb;
        _orchestrator = orchestrator;
        _recovery = recovery;
        _presenceStore = presenceStore;
        _companion = companion;
        _wireless = wireless;
        _persist = persist;
        _identity = identity;
        _presence = presence;
        _pairingStore = pairingStore;
        _settings = settings;
        _batteryGuard = batteryGuard;
        _logger = logger ?? Log.ForContext<ConnectionWatchdog>();

        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
    }

    public void Start(string ip, int port, int intervalSeconds = 10) =>
        Start(new WirelessWatchTarget { IpAddress = ip, Port = port, AutoReconnect = true }, intervalSeconds);

    public void Start(WirelessWatchTarget target, int intervalSeconds = 8)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.IpAddress);
        if (target.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(target), "Port geçersiz");
        ObjectDisposedException.ThrowIf(_disposed, this);

        var stable = string.IsNullOrWhiteSpace(target.StableId)
            ? _identity.ComputeStableId(target.DeviceId, target.SerialHint, null, target.DeviceModel)
            : target.StableId;

        var normalized = new WirelessWatchTarget
        {
            IpAddress = target.IpAddress,
            Port = target.Port,
            DeviceId = target.DeviceId,
            StableId = stable,
            SerialHint = target.SerialHint,
            DeviceName = target.DeviceName,
            DeviceModel = target.DeviceModel,
            Transport = target.Transport,
            AutoReconnect = target.AutoReconnect
        };

        var session = _sessions.AddOrUpdate(
            stable,
            _ => CreateSession(normalized, intervalSeconds),
            (_, existing) =>
            {
                existing.Restart(normalized, intervalSeconds);
                return existing;
            });

        _primaryStableId = stable;
        _logger.Information(
            "[WirelessGuardian] Hub Start Device={Stable} Sessions={Count} Endpoint={Ep}",
            stable, _sessions.Count, session.WatchedEndpoint);
    }

    private DeviceGuardianSession CreateSession(WirelessWatchTarget target, int intervalSeconds)
    {
        var session = new DeviceGuardianSession(
            target,
            intervalSeconds,
            _adb,
            _orchestrator,
            _recovery,
            _presenceStore,
            _persist,
            _presence,
            _pairingStore,
            _settings,
            _batteryGuard,
            ArmDiscoveryAsync,
            DisarmDiscoveryAsync,
            OnSessionStatus,
            OnSessionLost,
            OnSessionRestored,
            _logger);
        session.Start();
        return session;
    }

    public void StopDevice(string stableId)
    {
        if (string.IsNullOrWhiteSpace(stableId))
            return;

        if (_sessions.TryRemove(stableId, out var session))
        {
            session.Dispose();
            _logger.Information("[WirelessGuardian] Stopped Device={Stable} Remaining={Count}",
                stableId, _sessions.Count);
        }

        if (string.Equals(_primaryStableId, stableId, StringComparison.OrdinalIgnoreCase))
            _primaryStableId = _sessions.Keys.FirstOrDefault();

        if (_sessions.IsEmpty)
            PublishStatus(new WirelessLinkStatus
            {
                State = WirelessLinkState.Idle,
                Message = "İzleme durdu",
                Timestamp = DateTime.Now
            });
    }

    public bool IsWatchingDevice(string stableId) =>
        !string.IsNullOrWhiteSpace(stableId) && _sessions.ContainsKey(stableId);

    public void Stop()
    {
        foreach (var key in _sessions.Keys.ToArray())
            StopDevice(key);
    }

    public bool RequestReconnect(string? stableId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!string.IsNullOrWhiteSpace(stableId)
            && _sessions.TryGetValue(stableId, out var named))
            return named.RequestReconnect();

        if (_primaryStableId is not null
            && _sessions.TryGetValue(_primaryStableId, out var primary))
            return primary.RequestReconnect();

        var started = false;
        foreach (var session in _sessions.Values)
            started |= session.RequestReconnect();
        return started;
    }

    public DevicePresenceRecord? GetPresenceRecord(string? stableId = null)
    {
        var id = stableId ?? _primaryStableId;
        return string.IsNullOrWhiteSpace(id) ? null : _presenceStore.Get(id);
    }

    public async Task<bool> TryRecoverViaUsbAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_primaryStableId is null
            || !_sessions.TryGetValue(_primaryStableId, out var session))
            return false;

        return await session.TryRecoverViaUsbAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryOpenWirelessDebugSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _adb.ExecuteShellAsync(
                "am start -a android.settings.WIRELESS_DEBUGGING_SETTINGS 2>/dev/null || " +
                "am start -a android.settings.APPLICATION_DEVELOPMENT_SETTINGS",
                cancellationToken).ConfigureAwait(false);
            _logger.Information("[WirelessGuardian] Opened wireless debug settings");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[WirelessGuardian] Could not open wireless debug settings");
            return false;
        }
    }

    private async Task ArmDiscoveryAsync()
    {
        if (Interlocked.Increment(ref _discoveryRefCount) > 1)
            return;

        var s = _settings.Current;
        try
        {
            if (s.WirelessCompanionDiscovery)
                await _companion.StartListeningAsync().ConfigureAwait(false);
            if (s.WirelessMdnsDiscovery)
                await _wireless.StartScanningAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[Discovery] Failed to arm discovery");
        }
    }

    private Task DisarmDiscoveryAsync()
    {
        if (Interlocked.Decrement(ref _discoveryRefCount) > 0)
            return Task.CompletedTask;

        Interlocked.Exchange(ref _discoveryRefCount, 0);
        try
        {
            _companion.StopListening();
            _wireless.StopScanning();
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[Discovery] Failed to disarm");
        }

        return Task.CompletedTask;
    }

    private void OnSessionStatus(WirelessLinkStatus status)
    {
        // Hub UI is single-status: only the primary device updates CurrentStatus.
        if (!string.IsNullOrWhiteSpace(status.StableId)
            && !string.IsNullOrWhiteSpace(_primaryStableId)
            && !string.Equals(status.StableId, _primaryStableId, StringComparison.OrdinalIgnoreCase))
            return;

        PublishStatus(status);
    }

    private void OnSessionLost(string stableId)
    {
        _logger.Warning("[WirelessGuardian] ConnectionLost Device={Stable}", stableId);
        ConnectionLost?.Invoke(this, EventArgs.Empty);
    }

    private void OnSessionRestored(string ip)
    {
        ConnectionRestored?.Invoke(this, ip);
    }

    private void PublishStatus(WirelessLinkStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(this, status);
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        if (_disposed || _sessions.IsEmpty) return;
        _logger.Information("[Discovery] Network adapter changed — refreshing {Count} guardians",
            _sessions.Count);
        foreach (var session in _sessions.Values)
            session.NotifyNetworkChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        Stop();
        while (_discoveryRefCount > 0)
            ObservedTask.Run(DisarmDiscoveryAsync());
    }
}

/// <summary>Per-device guardian session — isolated reconnect loop.</summary>
internal sealed class DeviceGuardianSession : IDisposable
{
    private readonly IAdbService _adb;
    private readonly IWirelessConnectionOrchestrator _orchestrator;
    private readonly IWirelessDebugRecoveryService _recovery;
    private readonly IDevicePresenceStore _presenceStore;
    private readonly IConnectionPersistService _persist;
    private readonly IDevicePresenceService _presence;
    private readonly IPairingCredentialStore _pairingStore;
    private readonly ISettingsService _settings;
    private readonly ICompanionBatteryGuard _batteryGuard;
    private readonly Func<Task> _armDiscovery;
    private readonly Func<Task> _disarmDiscovery;
    private readonly Action<WirelessLinkStatus> _onStatus;
    private readonly Action<string> _onLost;
    private readonly Action<string> _onRestored;
    private readonly ILogger _logger;

    private Timer? _timer;
    private WirelessWatchTarget _target;
    private bool _wasConnected;
    private int _reconnectGate;
    private int _healthGate;
    private int _epoch;
    private int _missedChecks;
    private bool _discoveryArmed;
    private bool _disposed;
    private WirelessLinkState _lastNotifiedState = WirelessLinkState.Idle;
    private bool _isWatching;
    private int _intervalSeconds = 8;
    private int _lastNetworkGeneration = 0;

    public string StableId => _target.StableId;
    public string? WatchedEndpoint => _isWatching ? $"{_target.IpAddress}:{_target.Port}" : null;

    public DeviceGuardianSession(
        WirelessWatchTarget target,
        int intervalSeconds,
        IAdbService adb,
        IWirelessConnectionOrchestrator orchestrator,
        IWirelessDebugRecoveryService recovery,
        IDevicePresenceStore presenceStore,
        IConnectionPersistService persist,
        IDevicePresenceService presence,
        IPairingCredentialStore pairingStore,
        ISettingsService settings,
        ICompanionBatteryGuard batteryGuard,
        Func<Task> armDiscovery,
        Func<Task> disarmDiscovery,
        Action<WirelessLinkStatus> onStatus,
        Action<string> onLost,
        Action<string> onRestored,
        ILogger logger)
    {
        _target = target;
        _intervalSeconds = intervalSeconds;
        _adb = adb;
        _orchestrator = orchestrator;
        _recovery = recovery;
        _presenceStore = presenceStore;
        _persist = persist;
        _presence = presence;
        _pairingStore = pairingStore;
        _settings = settings;
        _batteryGuard = batteryGuard;
        _armDiscovery = armDiscovery;
        _disarmDiscovery = disarmDiscovery;
        _onStatus = onStatus;
        _onLost = onLost;
        _onRestored = onRestored;
        _logger = logger;
    }

    public async Task<bool> TryRecoverViaUsbAsync(CancellationToken cancellationToken = default)
    {
        SetStatus(WirelessLinkState.Reconnecting, "USB üzerinden kurtarma…", WatchedEndpoint);
        var ok = await _recovery.TryRecoverViaUsbAsync(_target, cancellationToken).ConfigureAwait(false);
        if (ok)
        {
            _wasConnected = true;
            _missedChecks = 0;
            SetStatus(WirelessLinkState.Connected, "USB kurtarma başarılı", WatchedEndpoint);
            _onRestored(_target.IpAddress);
        }
        else
        {
            SetStatus(WirelessLinkState.Failed, "USB kurtarma başarısız", WatchedEndpoint);
        }

        return ok;
    }

    public void Start()
    {
        _isWatching = true;
        _wasConnected = true;
        _missedChecks = 0;
        SetStatus(WirelessLinkState.Connected, "Wi‑Fi ADB bağlı", WatchedEndpoint);

        Observe(
            _batteryGuard.TryEnsureCompanionCanRunInBackgroundAsync(
                _adb.SelectedDevice?.Serial ?? _target.SerialHint),
            "CompanionBattery");

        // Immediate first probe, then one-shot reschedule after each cycle (no overlapping period).
        ScheduleNextHealthCheck(TimeSpan.FromMilliseconds(250));

        _logger.Information(
            "[WirelessGuardian] Device={Stable} State=Connected Endpoint={Ep}",
            _target.StableId, WatchedEndpoint);
    }

    public void Restart(WirelessWatchTarget target, int intervalSeconds)
    {
        Interlocked.Increment(ref _epoch);
        StopTimerAndDiscovery();
        Interlocked.Exchange(ref _reconnectGate, 0);
        _target = target;
        _intervalSeconds = intervalSeconds;
        Start();
    }

    public void NotifyNetworkChanged()
    {
        if (!_isWatching || _disposed) return;
        _presenceStore.InvalidateEndpoint(_target.StableId);
        Observe(ArmDiscoveryLocalAsync(), "ArmDiscovery-NetworkChange");
        if (!_wasConnected && Interlocked.CompareExchange(ref _reconnectGate, 0, 0) == 0)
            Observe(TryReconnectAsync(_epoch), "TryReconnect-NetworkChange");
    }

    private void CheckNetworkGenerationFromCompanion()
    {
        var record = _presenceStore.Get(_target.StableId);
        if (record is null || record.LastNetworkGeneration <= _lastNetworkGeneration)
            return;

        _lastNetworkGeneration = record.LastNetworkGeneration;
        _logger.Information(
            "[WirelessGuardian] Network generation changed ({Gen}) Device={Stable}",
            record.LastNetworkGeneration, _target.StableId);
        _presenceStore.InvalidateEndpoint(_target.StableId);
        if (_wasConnected)
        {
            _wasConnected = false;
            SetStatus(WirelessLinkState.Searching, "Ağ değişti — yeniden aranıyor…", WatchedEndpoint);
            _onLost(_target.StableId);
        }

        Observe(TryReconnectAsync(_epoch), "TryReconnect-NetworkGen");
    }

    public bool RequestReconnect()
    {
        if (!_isWatching || _disposed) return false;
        if (Interlocked.CompareExchange(ref _reconnectGate, 1, 0) != 0)
            return false;

        _missedChecks = 0;
        if (_wasConnected)
        {
            _wasConnected = false;
            _onLost(_target.StableId);
        }

        SetStatus(WirelessLinkState.Searching, "Manuel yeniden bağlanma…", WatchedEndpoint);
        Observe(TryReconnectAsync(_epoch, gateAlreadyHeld: true), "TryReconnect-Manual");
        return true;
    }

    private void ScheduleNextHealthCheck(TimeSpan delay)
    {
        if (_disposed || !_isWatching) return;

        _timer?.Dispose();
        _timer = new Timer(
            static state =>
            {
                var s = (DeviceGuardianSession)state!;
                s.Observe(s.RunHealthCycleAsync(s._epoch), "HealthCycle");
            },
            this,
            delay,
            Timeout.InfiniteTimeSpan);
    }

    private async Task RunHealthCycleAsync(int epoch)
    {
        if (!_isWatching || _disposed || epoch != Volatile.Read(ref _epoch))
            return;

        if (Interlocked.CompareExchange(ref _healthGate, 1, 0) != 0)
        {
            ScheduleNextHealthCheck(TimeSpan.FromSeconds(1));
            return;
        }

        try
        {
            // Avoid stacking health work on top of an in-flight reconnect.
            if (Interlocked.CompareExchange(ref _reconnectGate, 0, 0) != 0)
                return;

            await CheckConnectionAsync(epoch).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _healthGate, 0);
            if (_isWatching && !_disposed && epoch == Volatile.Read(ref _epoch))
                ScheduleNextHealthCheck(TimeSpan.FromSeconds(Math.Clamp(_intervalSeconds, 2, 60)));
        }
    }

    private async Task CheckConnectionAsync(int epoch)
    {
        if (!_isWatching || _disposed || epoch != Volatile.Read(ref _epoch))
            return;

        var autoReconnect = _settings.Current.WirelessAutoReconnect && _target.AutoReconnect;

        try
        {
            CheckNetworkGenerationFromCompanion();

            // Host-side ADB listing only — never shell/getprop on a possibly dead transport.
            var devices = await _adb.GetDevicesAsync().ConfigureAwait(false);
            if (epoch != Volatile.Read(ref _epoch) || !_isWatching || _disposed)
                return;

            // Manual/auto reconnect owns the state machine while the gate is held.
            if (Interlocked.CompareExchange(ref _reconnectGate, 0, 0) != 0)
                return;

            var online = devices.Where(d => d.IsOnline).ToList();
            var match = FindOnlineWirelessMatch(online);

            if (match is not null)
            {
                await AdoptEndpointIfChangedAsync(match.Serial).ConfigureAwait(false);
                if (epoch != Volatile.Read(ref _epoch)
                    || Interlocked.CompareExchange(ref _reconnectGate, 0, 0) != 0)
                    return;

                _missedChecks = 0;
                if (!_wasConnected)
                {
                    _wasConnected = true;
                    SetStatus(WirelessLinkState.Connected, "Yeniden bağlandı", WatchedEndpoint);
                    _onRestored(_target.IpAddress);
                    _logger.Information("[WirelessGuardian] Device={Stable} State=Connected", _target.StableId);
                }
                else if (_lastNotifiedState is WirelessLinkState.Unstable or WirelessLinkState.Searching
                         or WirelessLinkState.Reconnecting or WirelessLinkState.Discovering
                         or WirelessLinkState.Degraded)
                {
                    SetStatus(WirelessLinkState.Connected, "Wi‑Fi ADB bağlı", WatchedEndpoint);
                }

                await DisarmDiscoveryLocalAsync().ConfigureAwait(false);
                return;
            }

            _missedChecks++;
            if (_wasConnected && _missedChecks == 1)
            {
                SetStatus(WirelessLinkState.Degraded, "Bağlantı zayıf…", WatchedEndpoint);
                return;
            }

            if (_wasConnected && _missedChecks == 2)
            {
                SetStatus(WirelessLinkState.Unstable, "Bağlantı kararsız…", WatchedEndpoint);
                return;
            }

            if (_wasConnected)
            {
                _wasConnected = false;
                _logger.Warning("[WirelessGuardian] ADB connection lost Device={Stable} Endpoint={Ep}",
                    _target.StableId, WatchedEndpoint);
                SetStatus(WirelessLinkState.Searching, "ADB koptu — cihaz aranıyor…", WatchedEndpoint);
                _onLost(_target.StableId);

                if (autoReconnect)
                    Observe(TryReconnectAsync(epoch), "TryReconnect");
                else
                    SetStatus(WirelessLinkState.Failed, "Bağlantı koptu — otomatik yeniden bağlanma kapalı", WatchedEndpoint);

                return;
            }

            if (!autoReconnect)
                return;

            if (_lastNotifiedState is not (WirelessLinkState.Reconnecting or WirelessLinkState.NeedsWirelessDebug
                     or WirelessLinkState.AdbUnavailable or WirelessLinkState.PairingRequired)
                     && Interlocked.CompareExchange(ref _reconnectGate, 0, 0) == 0)
            {
                Observe(TryReconnectAsync(epoch), "TryReconnect");
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[WirelessGuardian] Health check failed Device={Stable}", _target.StableId);
        }
    }

    /// <summary>
    /// Wireless keep-alive: only <c>IP:PORT</c> ADB serials count. USB SerialHint must not
    /// mark the wireless link healthy when Wi‑Fi ADB is dead.
    /// </summary>
    private ConnectedDevice? FindOnlineWirelessMatch(IReadOnlyList<ConnectedDevice> online)
    {
        var endpoint = $"{_target.IpAddress}:{_target.Port}";

        var exact = online.FirstOrDefault(d =>
            d.Serial.Equals(endpoint, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        // Same host, possibly new TLS port after wireless-debug restart.
        return online.FirstOrDefault(d =>
            TryParseEndpoint(d.Serial, out var host, out _)
            && host.Equals(_target.IpAddress, StringComparison.OrdinalIgnoreCase));
    }

    private async Task AdoptEndpointIfChangedAsync(string adbSerial)
    {
        if (!TryParseEndpoint(adbSerial, out var host, out var port))
            return;

        if (!host.Equals(_target.IpAddress, StringComparison.OrdinalIgnoreCase)
            || port == _target.Port)
            return;

        var previous = WatchedEndpoint;
        _target = new WirelessWatchTarget
        {
            IpAddress = host,
            Port = port,
            DeviceId = _target.DeviceId,
            StableId = _target.StableId,
            SerialHint = _target.SerialHint,
            DeviceName = _target.DeviceName,
            DeviceModel = _target.DeviceModel,
            Transport = _target.Transport,
            AutoReconnect = _target.AutoReconnect
        };

        _logger.Information(
            "[WirelessGuardian] SoftEndpointAdopt Device={Stable} Old={Old} New={New}",
            _target.StableId, previous, WatchedEndpoint);

        await PersistEndpointAsync(EndpointDiscoverySource.Cached.ToString()).ConfigureAwait(false);

        if (_settings.Current.WirelessShowEndpointChanges)
        {
            SetStatus(
                WirelessLinkState.Connected,
                $"Uç nokta güncellendi ({previous} → {WatchedEndpoint})",
                WatchedEndpoint,
                previous);
        }
    }

    private async Task<WirelessWatchTarget> PrepareDiscoveryTargetAsync()
    {
        if (string.IsNullOrWhiteSpace(_target.IpAddress) || _target.Port is < 1 or > 65535)
            return _target;

        var alive = await EndpointProbeHelper.IsTcpOpenAsync(
            _target.IpAddress,
            _target.Port,
            EndpointProbeHelper.StaleEndpointTimeoutMs).ConfigureAwait(false);

        if (alive)
            return _target;

        _logger.Warning(
            "[WirelessGuardian] Stale endpoint {Ep} Device={Stable} — discovery without cache",
            WatchedEndpoint,
            _target.StableId);

        if (!string.IsNullOrWhiteSpace(_target.StableId))
            _presenceStore.InvalidateEndpoint(_target.StableId);

        return new WirelessWatchTarget
        {
            IpAddress = "",
            Port = 0,
            DeviceId = _target.DeviceId,
            StableId = _target.StableId,
            SerialHint = _target.SerialHint,
            DeviceName = _target.DeviceName,
            DeviceModel = _target.DeviceModel,
            Transport = _target.Transport,
            AutoReconnect = _target.AutoReconnect
        };
    }

    private static bool TryParseEndpoint(string serial, out string host, out int port)
    {
        host = "";
        port = 0;
        var idx = serial.LastIndexOf(':');
        if (idx <= 0 || idx >= serial.Length - 1)
            return false;

        host = serial[..idx];
        return int.TryParse(serial[(idx + 1)..], out port) && port is > 0 and <= 65535;
    }

    private async Task TryReconnectAsync(int epoch, bool gateAlreadyHeld = false)
    {
        var allowReconnect = gateAlreadyHeld
            || (_target.AutoReconnect && _settings.Current.WirelessAutoReconnect);

        if (!allowReconnect)
        {
            if (gateAlreadyHeld)
                Interlocked.Exchange(ref _reconnectGate, 0);
            return;
        }

        if (epoch != Volatile.Read(ref _epoch))
        {
            if (gateAlreadyHeld)
                Interlocked.Exchange(ref _reconnectGate, 0);
            return;
        }

        if (!gateAlreadyHeld && Interlocked.CompareExchange(ref _reconnectGate, 1, 0) != 0)
            return;

        try
        {
            if (epoch != Volatile.Read(ref _epoch) || !_isWatching || _disposed)
                return;

            await ArmDiscoveryLocalAsync().ConfigureAwait(false);
            SetStatus(WirelessLinkState.Discovering, "Keşif başlatıldı…", WatchedEndpoint);
            _logger.Information("[Discovery] mDNS search started Device={Stable}", _target.StableId);

            const int maxAttempts = 12;
            string? previous = WatchedEndpoint;
            var companionOnline = _presence.IsCompanionOnline(_target.DeviceId, _target.StableId);

            for (var attempt = 1; attempt <= maxAttempts && _isWatching && !_wasConnected; attempt++)
            {
                if (epoch != Volatile.Read(ref _epoch) || _disposed)
                    return;

                companionOnline = _presence.IsCompanionOnline(_target.DeviceId, _target.StableId);

                if (!companionOnline && attempt > 2)
                {
                    var presence = _presence.GetCompanionPresence(_target.DeviceId, _target.StableId);
                    if (presence == CompanionPresenceStatus.CompanionOffline)
                    {
                        SetStatus(
                            WirelessLinkState.DeviceOffline,
                            "Cihaz ağda bulunamadı",
                            previous,
                            previous,
                            presence);
                        await Task.Delay(WirelessGuardianBackoff.DelayForAttempt(attempt)).ConfigureAwait(false);
                        continue;
                    }
                }

                SetStatus(WirelessLinkState.Reconnecting, "Yeni uç nokta çözülüyor…", previous, previous,
                    companionOnline ? CompanionPresenceStatus.CompanionOnline : CompanionPresenceStatus.CompanionOffline);

                var discoveryTarget = await PrepareDiscoveryTargetAsync().ConfigureAwait(false);

                var resolved = await _orchestrator.ResolveAsync(discoveryTarget).ConfigureAwait(false);
                if (epoch != Volatile.Read(ref _epoch) || _disposed)
                    return;

                if (resolved is null)
                {
                    SetStatus(
                        WirelessLinkState.Searching,
                        $"Keşif {attempt}/{maxAttempts} — mDNS/Companion bekleniyor…",
                        previous,
                        previous,
                        companionOnline ? CompanionPresenceStatus.CompanionOnline : CompanionPresenceStatus.Unknown);
                    var delay = companionOnline
                        ? WirelessGuardianBackoff.FastDelayForCompanionOnline(attempt)
                        : WirelessGuardianBackoff.DelayForAttempt(attempt);
                    await Task.Delay(delay).ConfigureAwait(false);
                    continue;
                }

                if (!resolved.WirelessDebugLikelyEnabled && resolved.Source == EndpointDiscoverySource.Companion.ToString())
                {
                    SetStatus(
                        WirelessLinkState.AdbUnavailable,
                        "Cihaz ağda — Wireless Debugging bağlantısı kapalı",
                        resolved.Endpoint,
                        previous,
                        CompanionPresenceStatus.CompanionOnline);
                    await Task.Delay(WirelessGuardianBackoff.FastDelayForCompanionOnline(attempt)).ConfigureAwait(false);
                    continue;
                }

                _logger.Information("[Reconnect] Connecting Device={Stable} Endpoint={Ep} Source={Src}",
                    _target.StableId, resolved.Endpoint, resolved.Source);

                SetStatus(
                    WirelessLinkState.Reconnecting,
                    $"Bağlanılıyor ({resolved.Source}) {resolved.Endpoint}",
                    resolved.Endpoint,
                    previous);

                if (await _adb.ConnectWifiAsync(resolved.IpAddress, resolved.Port).ConfigureAwait(false))
                {
                    if (epoch != Volatile.Read(ref _epoch) || _disposed)
                        return;

                    var changed = !string.Equals(previous, resolved.Endpoint, StringComparison.OrdinalIgnoreCase);
                    if (changed)
                    {
                        _logger.Information(
                            "[WirelessGuardian] EndpointChanged Device={Stable} Old={Old} New={New}",
                            _target.StableId, previous, resolved.Endpoint);
                    }

                    _target = new WirelessWatchTarget
                    {
                        IpAddress = resolved.IpAddress,
                        Port = resolved.Port,
                        DeviceId = string.IsNullOrWhiteSpace(resolved.DeviceId) ? _target.DeviceId : resolved.DeviceId,
                        StableId = _target.StableId,
                        SerialHint = _target.SerialHint,
                        DeviceName = string.IsNullOrWhiteSpace(resolved.DeviceName) ? _target.DeviceName : resolved.DeviceName,
                        DeviceModel = _target.DeviceModel,
                        Transport = resolved.Transport,
                        AutoReconnect = true
                    };

                    await PersistEndpointAsync(resolved.Source).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(_target.StableId))
                        await _pairingStore.SetPairedAsync(_target.StableId).ConfigureAwait(false);

                    _wasConnected = true;
                    _missedChecks = 0;
                    var msg = changed && _settings.Current.WirelessShowEndpointChanges
                        ? $"Yeniden bağlandı ({previous} → {resolved.Endpoint})"
                        : changed
                            ? "Cihaz yeniden bağlandı"
                            : $"Yeniden bağlandı ({resolved.Endpoint})";
                    SetStatus(WirelessLinkState.Connected, msg, resolved.Endpoint, previous);
                    _logger.Information("[Reconnect] Success Device={Stable}", _target.StableId);
                    _onRestored(resolved.IpAddress);
                    return;
                }

                await Task.Delay(WirelessGuardianBackoff.DelayForAttempt(attempt)).ConfigureAwait(false);
            }

            if (!_wasConnected && epoch == Volatile.Read(ref _epoch))
            {
                var pairing = !string.IsNullOrWhiteSpace(_target.StableId)
                    ? await _pairingStore.GetPairingStateAsync(_target.StableId).ConfigureAwait(false)
                    : PairingState.Unknown;

                var presenceRecord = _presenceStore.Get(_target.StableId);
                var scenario = _orchestrator.InferScenario(_target, presenceRecord);

                if (pairing == PairingState.PairingRequired)
                {
                    SetStatus(
                        WirelessLinkState.PairingRequired,
                        "Wireless Debugging eşleştirmesi gerekiyor",
                        endpoint: previous,
                        scenario: WirelessDiagnosticScenario.PairingRequired,
                        presenceRecord: presenceRecord);
                }
                else if (scenario == WirelessDiagnosticScenario.DeviceOnCellular)
                {
                    SetStatus(
                        WirelessLinkState.DeviceOffline,
                        $"Son görülme: {presenceRecord?.LastSeenDisplay ?? "bilinmiyor"} — mobil veri (ADB Wi‑Fi gerektirir)",
                        endpoint: previous,
                        scenario: scenario,
                        presenceRecord: presenceRecord);
                }
                else if (companionOnline)
                {
                    SetStatus(
                        WirelessLinkState.AdbUnavailable,
                        "Cihaz ağda — Kablosuz hata ayıklama kapalı",
                        endpoint: previous,
                        companionPresence: CompanionPresenceStatus.CompanionOnline,
                        scenario: WirelessDiagnosticScenario.WirelessDebugOff,
                        presenceRecord: presenceRecord);
                }
                else
                {
                    SetStatus(
                        WirelessLinkState.Failed,
                        $"Cihaz ağda bulunamadı — son görülme: {presenceRecord?.LastSeenDisplay ?? "bilinmiyor"}",
                        endpoint: previous,
                        scenario: WirelessDiagnosticScenario.DeviceOffline,
                        presenceRecord: presenceRecord);
                }

                _logger.Error("[WirelessGuardian] Reconnect failed Device={Stable}", _target.StableId);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _reconnectGate, 0);
        }
    }

    private async Task ArmDiscoveryLocalAsync()
    {
        if (_discoveryArmed) return;
        await _armDiscovery().ConfigureAwait(false);
        _discoveryArmed = true;
    }

    private async Task DisarmDiscoveryLocalAsync()
    {
        if (!_discoveryArmed) return;
        _discoveryArmed = false;
        await _disarmDiscovery().ConfigureAwait(false);
    }

    private async Task PersistEndpointAsync(string source)
    {
        try
        {
            Enum.TryParse<EndpointDiscoverySource>(source, true, out var discoverySource);
            await _persist.SaveAsync(new SavedConnection
            {
                DeviceId = _target.DeviceId,
                StableDeviceId = _target.StableId,
                Serial = _target.SerialHint,
                IpAddress = _target.IpAddress,
                Port = _target.Port,
                DeviceName = _target.DeviceName,
                DeviceModel = _target.DeviceModel,
                Transport = _target.Transport,
                AutoReconnect = true,
                DiscoverySource = discoverySource,
                LastSeen = DateTime.Now,
                PairingState = PairingState.Paired
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[WirelessGuardian] Persist failed Device={Stable}", _target.StableId);
        }
    }

    private void SetStatus(
        WirelessLinkState state,
        string message,
        string? endpoint = null,
        string? previous = null,
        CompanionPresenceStatus companionPresence = CompanionPresenceStatus.Unknown,
        WirelessDiagnosticScenario scenario = WirelessDiagnosticScenario.Unknown,
        DevicePresenceRecord? presenceRecord = null)
    {
        var status = new WirelessLinkStatus
        {
            State = state,
            Message = message,
            Endpoint = endpoint,
            PreviousEndpoint = previous,
            DeviceName = _target.DeviceName,
            StableId = _target.StableId,
            CompanionPresence = companionPresence,
            Scenario = scenario,
            LastSeen = presenceRecord?.LastSeen == DateTime.MinValue ? null : presenceRecord?.LastSeen,
            LastNetworkType = presenceRecord?.LastNetworkType,
            Timestamp = DateTime.Now
        };

        if (_lastNotifiedState != state)
        {
            _lastNotifiedState = state;
            _logger.Information("[WirelessGuardian] Device={Stable} State={State} Msg={Msg}",
                _target.StableId, state, message);
        }

        _onStatus(status);
    }

    private void Observe(Task task, string operation)
    {
        _ = task.ContinueWith(
            t =>
            {
                if (t.IsFaulted && t.Exception is not null)
                    _logger.Warning(t.Exception.Flatten(),
                        "[WirelessGuardian] Unobserved {Op} Device={Stable}", operation, _target.StableId);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void StopTimerAndDiscovery()
    {
        _isWatching = false;
        _timer?.Dispose();
        _timer = null;
        Interlocked.Exchange(ref _healthGate, 0);
        if (_discoveryArmed)
        {
            _discoveryArmed = false;
            try { _ = _disarmDiscovery(); } catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Increment(ref _epoch);
        StopTimerAndDiscovery();
    }
}
