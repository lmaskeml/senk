using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Device.Services;

public sealed class CompanionDiscoveryService : ICompanionDiscoveryService
{
    private readonly IAdbService _adb;
    private readonly IDeviceIdentityService _identity;
    private readonly IDevicePresenceStore _presenceStore;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, CompanionDevice> _cache = new(StringComparer.OrdinalIgnoreCase);
    private UdpClient? _udpClient;
    private CancellationTokenSource? _listenCts;
    private Task? _listenTask;
    private Task? _scanTask;
    private bool _disposed;

    public event EventHandler<CompanionDevice>? DeviceDiscovered;

    public CompanionDiscoveryService(
        IAdbService adb,
        IDeviceIdentityService identity,
        IDevicePresenceStore presenceStore,
        ILogger? logger = null)
    {
        _adb = adb;
        _identity = identity;
        _presenceStore = presenceStore;
        _logger = logger ?? Log.ForContext<CompanionDiscoveryService>();
    }

    public IReadOnlyList<CompanionDevice> GetCachedDevices()
    {
        var cutoff = DateTime.Now.AddSeconds(-90);
        return _cache.Values
            .Where(d => d.DiscoveredAt >= cutoff)
            .OrderByDescending(d => d.DiscoveredAt)
            .ToList();
    }

    public Task StartListeningAsync(CancellationToken cancellationToken = default)
    {
        StopListening();

        // Watchdog may have already swallowed identical UDP heartbeats. A new
        // listen session (Wi‑Fi dialog) must re-raise so the UI is not empty.
        _lastFingerprints.Clear();

        _listenCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _listenCts.Token;

        _udpClient = CreateUdpListener(CompanionPorts.UdpBroadcast);
        _logger.Information("Companion discovery listening on UDP:{Port}", CompanionPorts.UdpBroadcast);

        _listenTask = Task.Run(() => ListenLoopAsync(ct), ct);
        _scanTask = Task.Run(() => LanProbeLoopAsync(ct), ct);

        var replay = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var cached in GetCachedDevices())
            RaiseIfNew(replay, cached);

        return Task.CompletedTask;
    }

    private static UdpClient CreateUdpListener(int port)
    {
        var client = new UdpClient();
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        client.EnableBroadcast = true;
        return client;
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        var seen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_udpClient is null) break;

                var result = await _udpClient.ReceiveAsync(ct).ConfigureAwait(false);
                var json = Encoding.UTF8.GetString(result.Buffer);
                var device = ParseCompanionPayload(json);
                if (device is null) continue;

                // Prefer sender IP if payload IP is wrong/missing
                if (string.IsNullOrWhiteSpace(device.IpAddress) ||
                    device.IpAddress.Contains("Bağlı", StringComparison.OrdinalIgnoreCase) ||
                    device.IpAddress is "0.0.0.0")
                {
                    device = CloneCompanion(device, result.RemoteEndPoint.Address.ToString());
                }

                // Probe ADB so UI does not imply connect will work
                if (!device.IsAdbReachable)
                {
                    var probePort = device.EffectiveAdbPort;
                    var open = await IsTcpOpenAsync(device.IpAddress, probePort, 200, ct).ConfigureAwait(false);
                    if (!open && probePort != CompanionPorts.DefaultAdb)
                        open = await IsTcpOpenAsync(device.IpAddress, CompanionPorts.DefaultAdb, 200, ct)
                            .ConfigureAwait(false);

                    device = CloneCompanion(device, device.IpAddress, open);
                }

                RaiseIfNew(seen, device);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    _logger.Warning(ex, "UDP receive error");
            }
        }
    }

    /// <summary>
    /// Fallback when UDP broadcast is blocked (AP isolation): probe local /24 for open TCP pair port.
    /// </summary>
    private async Task LanProbeLoopAsync(CancellationToken ct)
    {
        var seen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // Immediate pass, then every 8s
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProbeLocalSubnetsAsync(seen, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.Debug(ex, "LAN probe pass failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProbeLocalSubnetsAsync(Dictionary<string, DateTime> seen, CancellationToken ct)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prefix in GetLocalIpv4Prefixes())
        {
            foreach (var ip in ArpTableReader.GetActiveIpv4Hosts(prefix))
                candidates.Add(ip);
        }

        if (candidates.Count == 0)
            return;

        _logger.Debug("LAN ARP probe on {Count} host(s)", candidates.Count);

        using var gate = new SemaphoreSlim(10);
        var tasks = candidates.Select(ip => ProbeHostAsync(ip, gate, seen, ct)).ToList();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on stop
        }
    }

    private async Task ProbeHostAsync(
        string ip,
        SemaphoreSlim gate,
        Dictionary<string, DateTime> seen,
        CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await IsTcpOpenAsync(ip, CompanionPorts.TcpPairing, 180, ct).ConfigureAwait(false))
            {
                var adbOpen = await IsTcpOpenAsync(ip, CompanionPorts.DefaultAdb, 180, ct)
                    .ConfigureAwait(false);
                RaiseIfNew(seen, new CompanionDevice
                {
                    IpAddress = ip,
                    AdbPort = CompanionPorts.DefaultAdb,
                    TcpPort = CompanionPorts.TcpPairing,
                    DeviceName = $"Companion @ {ip}",
                    DeviceModel = "LAN",
                    AndroidVersion = "?",
                    IsAdbReachable = adbOpen,
                    DiscoveredAt = DateTime.Now
                });
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<bool> IsTcpOpenAsync(string ip, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeoutMs);
            await client.ConnectAsync(IPAddress.Parse(ip), port, linked.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> GetLocalIpv4Prefixes()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var bytes = ua.Address.GetAddressBytes();
                if (bytes[0] == 127) continue;
                yield return $"{bytes[0]}.{bytes[1]}.{bytes[2]}";
            }
        }
    }

    private readonly ConcurrentDictionary<string, string> _lastFingerprints = new(StringComparer.OrdinalIgnoreCase);

    private void RaiseIfNew(Dictionary<string, DateTime> seen, CompanionDevice device)
    {
        if (string.IsNullOrWhiteSpace(device.IpAddress)) return;

        var key = string.IsNullOrWhiteSpace(device.DeviceId) ? device.IpAddress : device.DeviceId;
        _cache[key] = device;
        _cache[device.IpAddress] = device;

        var fp = $"{device.IpAddress}|{device.EffectiveAdbPort}|{device.WirelessAdbPort}|{device.WirelessDebugEnabled}|{device.NetworkGeneration}|{device.NetworkType}";
        var changed = !_lastFingerprints.TryGetValue(key, out var prev) ||
                      !string.Equals(prev, fp, StringComparison.Ordinal);
        _lastFingerprints[key] = fp;
        if (!changed)
            return;

        var stableId = _identity.ComputeStableId(device.DeviceId, null, device.Manufacturer, device.DeviceModel);
        if (!string.IsNullOrWhiteSpace(stableId))
            _presenceStore.RecordCompanionHeartbeat(device, stableId);

        DeviceDiscovered?.Invoke(this, device);
        _logger.Information(
            "Companion: {Name} @ {Ip}:{Port} (wireless={W})",
            device.DeviceName, device.IpAddress, device.EffectiveAdbPort, device.WirelessDebugEnabled);
    }

    private static CompanionDevice CloneCompanion(
        CompanionDevice source,
        string ip,
        bool? isAdbReachable = null) =>
        new()
        {
            DeviceId = source.DeviceId,
            IpAddress = ip,
            AdbPort = source.AdbPort,
            WirelessAdbPort = source.WirelessAdbPort,
            WirelessPairingPort = source.WirelessPairingPort,
            TcpPort = source.TcpPort,
            DeviceName = source.DeviceName,
            DeviceModel = source.DeviceModel,
            AndroidVersion = source.AndroidVersion,
            WirelessDebugEnabled = source.WirelessDebugEnabled,
            PortSource = source.PortSource,
            NetworkType = source.NetworkType,
            NetworkGeneration = source.NetworkGeneration,
            Manufacturer = source.Manufacturer,
            IsAdbReachable = isAdbReachable ?? source.IsAdbReachable,
            DiscoveredAt = DateTime.Now
        };

    public async Task<CompanionConnectResult> ConnectWithPairCodeAsync(
        string ip,
        int tcpPort,
        string pairCode,
        string pcName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.Information("Pairing with companion {Ip}:{Port}", ip, tcpPort);

            using var client = new TcpClient();
            await client.ConnectAsync(ip, tcpPort, cancellationToken).ConfigureAwait(false);

            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            var request = JsonSerializer.Serialize(new
            {
                pairCode,
                pcName,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
            await writer.WriteLineAsync(request.AsMemory(), cancellationToken).ConfigureAwait(false);

            var response = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response))
                return Fail("Yanıt alınamadı");

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? string.Empty : string.Empty;

            if (!string.Equals(status, "approved", StringComparison.OrdinalIgnoreCase))
            {
                var reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "Reddedildi" : "Reddedildi";
                return Fail(reason);
            }

            var deviceIp = root.TryGetProperty("ip", out var ipEl) ? ipEl.GetString() ?? ip : ip;
            var devicePort = root.TryGetProperty("port", out var portEl) ? portEl.GetInt32() : CompanionPorts.DefaultAdb;
            var wirelessPort = root.TryGetProperty("wirelessAdbPort", out var wEl) && wEl.TryGetInt32(out var wp)
                ? wp
                : (int?)null;
            var deviceName = root.TryGetProperty("device", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
            var deviceId = root.TryGetProperty("deviceId", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;

            if (string.IsNullOrWhiteSpace(deviceIp) || deviceIp.Contains("Bağlı", StringComparison.OrdinalIgnoreCase))
                deviceIp = ip;

            // Companion pair ≠ ADB listening. Try wireless TLS port first, then classic 5555.
            var candidatePorts = new List<int>();
            if (wirelessPort is > 0)
                candidatePorts.Add(wirelessPort.Value);
            if (devicePort > 0)
                candidatePorts.Add(devicePort);
            if (!candidatePorts.Contains(CompanionPorts.DefaultAdb))
                candidatePorts.Add(CompanionPorts.DefaultAdb);

            foreach (var port in candidatePorts.Distinct())
            {
                if (!await IsTcpOpenAsync(deviceIp, port, 400, cancellationToken).ConfigureAwait(false))
                {
                    _logger.Information("ADB port closed: {Ip}:{Port}", deviceIp, port);
                    continue;
                }

                var adbOk = await _adb.ConnectWifiAsync(deviceIp, port, cancellationToken).ConfigureAwait(false);
                if (adbOk)
                {
                    return new CompanionConnectResult
                    {
                        Success = true,
                        Message = "Bağlantı başarılı",
                        DeviceName = deviceName,
                        DeviceId = deviceId,
                        IpAddress = deviceIp,
                        Port = port,
                        Transport = wirelessPort is > 0 && port == wirelessPort
                            ? WirelessTransportKind.WirelessTls
                            : WirelessTransportKind.LegacyTcp
                    };
                }
            }

            return Fail(
                "Companion eşleşti ama ADB portu kapalı (5555 reddedildi).\n\n" +
                "Android 11+: Geliştirici seçenekleri → Kablosuz hata ayıklama açın, " +
                "sonra bu pencerede Otomatik taramada mDNS cihazına bağlanın " +
                "veya USB bağlayıp «USB → WiFi» kullanın.\n\n" +
                "Not: Listede Companion görünmesi yalnızca eşleştirme servisinin açık olduğunu gösterir; " +
                "ADB dinlemesi ayrıdır.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Pair code connect failed");
            return Fail(ex.Message);
        }
    }

    public async Task<CompanionConnectResult> ConnectFromQrAsync(
        string qrContent,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(qrContent);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!string.Equals(type, "AndroidManagerConnect", StringComparison.Ordinal))
                return Fail("Geçersiz QR kod");

            var ip = root.GetProperty("ip").GetString() ?? string.Empty;
            var pairCode = root.GetProperty("pairCode").GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(pairCode))
                return Fail("QR içeriği eksik");

            return await ConnectWithPairCodeAsync(
                    ip,
                    CompanionPorts.TcpPairing,
                    pairCode,
                    Environment.MachineName,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Fail($"QR parse hatası: {ex.Message}");
        }
    }

    public void StopListening()
    {
        try
        {
            _listenCts?.Cancel();
            _udpClient?.Close();
            _udpClient?.Dispose();
            _udpClient = null;
            _listenCts?.Dispose();
            _listenCts = null;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "StopListening error");
        }
    }

    private static CompanionDevice? ParseCompanionPayload(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!string.Equals(type, "AndroidManagerCompanion", StringComparison.Ordinal))
                return null;

            return new CompanionDevice
            {
                DeviceId = root.TryGetProperty("deviceId", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty,
                IpAddress = root.GetProperty("ipAddress").GetString() ?? string.Empty,
                AdbPort = root.TryGetProperty("adbPort", out var ap) ? ap.GetInt32() : 5555,
                WirelessAdbPort = root.TryGetProperty("wirelessAdbPort", out var wap) && wap.TryGetInt32(out var wp)
                    ? wp
                    : null,
                WirelessPairingPort = root.TryGetProperty("wirelessPairingPort", out var wpp) && wpp.TryGetInt32(out var pport)
                    ? pport
                    : null,
                TcpPort = root.TryGetProperty("tcpPort", out var tp) ? tp.GetInt32() : CompanionPorts.TcpPairing,
                DeviceName = root.TryGetProperty("deviceName", out var dn) ? dn.GetString() ?? string.Empty : string.Empty,
                DeviceModel = root.TryGetProperty("deviceModel", out var dm) ? dm.GetString() ?? string.Empty : string.Empty,
                AndroidVersion = root.TryGetProperty("androidVersion", out var av) ? av.GetString() ?? string.Empty : string.Empty,
                WirelessDebugEnabled = root.TryGetProperty("wirelessDebugEnabled", out var wde) &&
                                       wde.ValueKind is JsonValueKind.True,
                PortSource = root.TryGetProperty("wirelessPortSource", out var ps)
                    ? ps.GetString() ?? ""
                    : root.TryGetProperty("portSource", out var ps2) ? ps2.GetString() ?? "" : "",
                NetworkType = root.TryGetProperty("networkType", out var nt) ? nt.GetString() ?? "" : "",
                NetworkGeneration = root.TryGetProperty("networkGeneration", out var ng) && ng.TryGetInt32(out var gen)
                    ? gen
                    : 0,
                Manufacturer = root.TryGetProperty("manufacturer", out var mf) ? mf.GetString() ?? string.Empty : string.Empty,
                SdkVersion = root.TryGetProperty("sdkVersion", out var sdk) && sdk.TryGetInt32(out var sv) ? sv : 0,
                CompanionVersion = root.TryGetProperty("companionVersion", out var cv) ? cv.GetString() ?? string.Empty : string.Empty,
                Capabilities = ParseCapabilities(root),
                DiscoveredAt = DateTime.Now
            };
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ParseCapabilities(JsonElement root)
    {
        if (!root.TryGetProperty("capabilities", out var cap) || cap.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var list = new List<string>();
        foreach (var item in cap.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    list.Add(s);
            }
        }

        return list;
    }

    private static CompanionConnectResult Fail(string message) => new()
    {
        Success = false,
        Message = message
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopListening();
    }
}
