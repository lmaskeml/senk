using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Device.Services;

public sealed partial class WirelessDebugDiscoveryService : IWirelessDebugDiscoveryService
{
    private readonly IAdbService _adb;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, byte> _hintedEndpoints = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _scanCts;
    private Task? _scanTask;
    private bool _disposed;

    public event EventHandler<WirelessDebugDevice>? DeviceDiscovered;

    public WirelessDebugDiscoveryService(IAdbService adb, ILogger? logger = null)
    {
        _adb = adb;
        _logger = logger ?? Log.ForContext<WirelessDebugDiscoveryService>();
    }

    public Task StartScanningAsync(CancellationToken cancellationToken = default)
    {
        StopScanning();
        AdbService.ApplyMdnsEnvironment();
        _scanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _scanCts.Token;
        _scanTask = Task.Run(() => PollLoopAsync(ct), ct);
        return Task.CompletedTask;
    }

    public void StopScanning()
    {
        try { _scanCts?.Cancel(); } catch { /* ignore */ }
        _scanCts?.Dispose();
        _scanCts = null;
        _scanTask = null;
    }

    public void HintKnownEndpoints(IReadOnlyList<(string Ip, int Port)> endpoints)
    {
        foreach (var (ip, port) in endpoints)
        {
            if (string.IsNullOrWhiteSpace(ip) || port <= 0)
                continue;

            var trimmed = ip.Trim();
            _hintedEndpoints.TryAdd($"{trimmed}:{port}", 0);
            if (port != CompanionPorts.DefaultAdb)
                _hintedEndpoints.TryAdd($"{trimmed}:{CompanionPorts.DefaultAdb}", 0);
        }
    }

    public async Task<IReadOnlyList<MdnsAdbEndpoint>> ListMdnsEndpointsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_adb.IsRunning)
            await _adb.StartAsync(cancellationToken).ConfigureAwait(false);

        var output = await RunAdbAsync("mdns services", cancellationToken).ConfigureAwait(false);
        return ParseMdnsOutput(output);
    }

    public async Task<IReadOnlyList<WirelessDebugDevice>> GetDiscoveredDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var endpoints = await ListMdnsEndpointsAsync(cancellationToken).ConfigureAwait(false);
        return GroupDevices(endpoints);
    }

    public async Task<CompanionConnectResult> ConnectAsync(
        WirelessDebugDevice device,
        string? pairCode = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_adb.IsRunning)
                await _adb.StartAsync(cancellationToken).ConfigureAwait(false);

            // Pair first when pairing service is up and a code was provided,
            // or when we cannot connect yet and only pairing is available.
            if (device.HasPairingService
                && (!string.IsNullOrWhiteSpace(pairCode) || !device.CanConnectDirectly))
            {
                if (string.IsNullOrWhiteSpace(pairCode))
                {
                    return new CompanionConnectResult
                    {
                        Success = false,
                        Message = "Telefonda “Eşleştirme koduyla cihaz eşleştir” açık olsun ve 6 haneli kodu girin.",
                        IpAddress = device.IpAddress,
                        Port = device.PairingPort ?? 0,
                        DeviceName = device.DisplayName
                    };
                }

                var paired = await _adb.PairWifiAsync(
                        device.IpAddress,
                        device.PairingPort!.Value,
                        pairCode.Trim(),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!paired)
                {
                    return new CompanionConnectResult
                    {
                        Success = false,
                        Message = "adb pair başarısız. Kod / port güncel mi kontrol edin.",
                        IpAddress = device.IpAddress,
                        Port = device.PairingPort ?? 0,
                        DeviceName = device.DisplayName
                    };
                }

                _logger.Information("Paired wireless debug device {Id}", device.DeviceId);
                await Task.Delay(700, cancellationToken).ConfigureAwait(false);
            }

            var connectPort = device.ConnectPort;
            if (connectPort is null or <= 0)
            {
                // After pairing, wait briefly for _adb-tls-connect to appear.
                for (var i = 0; i < 8 && (connectPort is null or <= 0); i++)
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    var refreshed = (await GetDiscoveredDevicesAsync(cancellationToken).ConfigureAwait(false))
                        .FirstOrDefault(d =>
                            string.Equals(d.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(d.IpAddress, device.IpAddress, StringComparison.OrdinalIgnoreCase));
                    connectPort = refreshed?.ConnectPort;
                }
            }

            if (connectPort is null or <= 0)
            {
                return new CompanionConnectResult
                {
                    Success = false,
                    Message = "Eşleştirme tamam; bağlanılacak mDNS portu henüz görünmüyor. Kablosuz hata ayıklama açık kalsın ve tekrar deneyin.",
                    IpAddress = device.IpAddress,
                    DeviceName = device.DisplayName
                };
            }

            var ok = await _adb.ConnectWifiAsync(device.IpAddress, connectPort.Value, cancellationToken)
                .ConfigureAwait(false);

            return new CompanionConnectResult
            {
                Success = ok,
                Message = ok
                    ? $"Kablosuz hata ayıklama bağlandı: {device.IpAddress}:{connectPort}"
                    : $"adb connect {device.IpAddress}:{connectPort} başarısız. Daha önce eşleştirildi mi?",
                IpAddress = device.IpAddress,
                Port = connectPort.Value,
                DeviceName = device.DisplayName,
                DeviceId = device.DeviceId,
                Transport = device.IsLegacyTcp
                    ? WirelessTransportKind.LegacyTcp
                    : WirelessTransportKind.WirelessTls
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Wireless debug connect failed");
            return new CompanionConnectResult
            {
                Success = false,
                Message = ex.Message,
                IpAddress = device.IpAddress,
                DeviceName = device.DisplayName
            };
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var round = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var devices = (await GetDiscoveredDevicesAsync(ct).ConfigureAwait(false)).ToList();

                // Android 9 tcpip 5555 often never shows in `adb mdns services`.
                if (round++ % 4 == 0)
                {
                    foreach (var legacy in await ProbeLegacyAdbAsync(ct).ConfigureAwait(false))
                    {
                        if (devices.Any(d =>
                                string.Equals(d.IpAddress, legacy.IpAddress, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        devices.Add(legacy);
                    }
                }

                foreach (var device in devices)
                {
                    var key = $"{device.DeviceId}|{device.IpAddress}|{device.ConnectPort}|{device.PairingPort}";
                    if (!seen.Add(key))
                        continue;
                    DeviceDiscovered?.Invoke(this, device);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    _logger.Debug(ex, "mDNS poll failed");
            }

            try
            {
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<string> RunAdbAsync(string arguments, CancellationToken cancellationToken)
    {
        var adbPath = AdbService.ResolveAdbPath();
        if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath))
            throw new FileNotFoundException("adb.exe bulunamadı.", adbPath);

        var psi = new ProcessStartInfo
        {
            FileName = adbPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["ADB_MDNS_OPENSCREEN"] = "1";
        psi.Environment["ADB_MDNS"] = "1";

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException($"adb {arguments} başlatılamadı.");
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stderr) ? $"adb {arguments} exit {process.ExitCode}" : stderr.Trim());

        return stdout;
    }

    public static IReadOnlyList<MdnsAdbEndpoint> ParseMdnsOutput(string output)
    {
        var list = new List<MdnsAdbEndpoint>();
        if (string.IsNullOrWhiteSpace(output))
            return list;

        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("List of", StringComparison.OrdinalIgnoreCase))
                continue;

            var match = MdnsLineRegex().Match(line);
            if (!match.Success)
                match = MdnsLineLooseRegex().Match(line);
            if (!match.Success)
                continue;

            var serviceType = match.Groups["type"].Value.Trim().TrimEnd('.');
            var kind = Classify(serviceType);
            if (kind is null)
                continue;

            if (!int.TryParse(match.Groups["port"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
                continue;

            list.Add(new MdnsAdbEndpoint
            {
                InstanceName = match.Groups["name"].Value.Trim(),
                ServiceType = serviceType,
                Kind = kind.Value,
                IpAddress = match.Groups["ip"].Value.Trim(),
                Port = port
            });
        }

        return list
            .GroupBy(e => $"{e.InstanceName}|{e.ServiceType}|{e.IpAddress}|{e.Port}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    public static IReadOnlyList<WirelessDebugDevice> GroupDevices(IReadOnlyList<MdnsAdbEndpoint> endpoints)
    {
        // Prefer grouping by IP — instance suffixes differ between pairing/connect.
        var groups = endpoints.GroupBy(e => e.IpAddress, StringComparer.OrdinalIgnoreCase);
        var devices = new List<WirelessDebugDevice>();

        foreach (var group in groups)
        {
            var connect = group.FirstOrDefault(e => e.Kind == MdnsServiceKind.TlsConnect)
                          ?? group.FirstOrDefault(e => e.Kind == MdnsServiceKind.LegacyAdb);
            var pairing = group.FirstOrDefault(e => e.Kind == MdnsServiceKind.TlsPairing);
            if (connect is null && pairing is null)
                continue;

            var idSource = connect?.InstanceName ?? pairing!.InstanceName;
            var deviceId = NormalizeDeviceId(idSource);
            devices.Add(new WirelessDebugDevice
            {
                DeviceId = deviceId,
                DisplayName = deviceId,
                IpAddress = group.Key,
                ConnectPort = connect?.Port,
                PairingPort = pairing?.Port,
                IsLegacyTcp = connect?.Kind == MdnsServiceKind.LegacyAdb
            });
        }

        return devices;
    }

    private static string NormalizeDeviceId(string instanceName)
    {
        // adb-SERIAL-random or adb-SERIAL
        var name = instanceName;
        var idx = name.IndexOf("._", StringComparison.Ordinal);
        if (idx > 0)
            name = name[..idx];

        var parts = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0].Equals("adb", StringComparison.OrdinalIgnoreCase))
            return $"adb-{parts[1]}";

        return name;
    }

    private async Task<List<WirelessDebugDevice>> ProbeLegacyAdbAsync(CancellationToken ct)
    {
        var localIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        localIps.Add(ua.Address.ToString());
                }
            }
        }
        catch
        {
            // ignore
        }

        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ip in ArpTableReader.GetActiveIpv4Hosts())
        {
            if (!localIps.Contains(ip))
                hosts.Add($"{ip}:{CompanionPorts.DefaultAdb}");
        }

        foreach (var endpoint in _hintedEndpoints.Keys)
        {
            var sep = endpoint.LastIndexOf(':');
            var ip = sep > 0 ? endpoint[..sep] : endpoint;
            if (!localIps.Contains(ip))
                hosts.Add(endpoint);
        }

        if (hosts.Count == 0)
            return [];

        using var gate = new SemaphoreSlim(12);
        var tasks = hosts.Select(endpoint => ProbeLegacyHostAsync(endpoint, gate, ct)).ToList();
        try
        {
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return results.Where(d => d is not null).Cast<WirelessDebugDevice>().ToList();
        }
        catch (OperationCanceledException)
        {
            return [];
        }
    }

    private static async Task<WirelessDebugDevice?> ProbeLegacyHostAsync(
        string endpoint,
        SemaphoreSlim gate,
        CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sep = endpoint.LastIndexOf(':');
            if (sep <= 0 || sep == endpoint.Length - 1)
                return null;

            var ip = endpoint[..sep];
            if (!int.TryParse(endpoint[(sep + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                || port <= 0)
                return null;

            if (IPAddress.TryParse(ip, out var addr) && addr.AddressFamily != AddressFamily.InterNetwork)
                return null;

            if (!await IsTcpOpenAsync(ip, port, 160, ct).ConfigureAwait(false))
                return null;

            return new WirelessDebugDevice
            {
                DeviceId = $"adb-{ip.Replace('.', '-')}",
                DisplayName = $"ADB {ip}",
                IpAddress = ip,
                ConnectPort = port,
                IsLegacyTcp = port == CompanionPorts.DefaultAdb
            };
        }
        catch (OperationCanceledException)
        {
            return null;
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

    private static MdnsServiceKind? Classify(string serviceType)
    {
        if (serviceType.Contains("adb-tls-pairing", StringComparison.OrdinalIgnoreCase))
            return MdnsServiceKind.TlsPairing;
        if (serviceType.Contains("adb-tls-connect", StringComparison.OrdinalIgnoreCase))
            return MdnsServiceKind.TlsConnect;
        if (serviceType.Contains("_adb._tcp", StringComparison.OrdinalIgnoreCase)
            || serviceType.Equals("_adb._tcp", StringComparison.OrdinalIgnoreCase)
            || serviceType.StartsWith("_adb._tcp", StringComparison.OrdinalIgnoreCase))
            return MdnsServiceKind.LegacyAdb;

        // adb mdns prints type as "_adb._tcp" without extra suffix sometimes as bare token
        if (serviceType.Equals("_adb._tcp", StringComparison.OrdinalIgnoreCase)
            || serviceType.Equals("adb._tcp", StringComparison.OrdinalIgnoreCase))
            return MdnsServiceKind.LegacyAdb;

        // Line format uses tab-separated "_adb._tcp"
        if (serviceType is "_adb._tcp" or "_adb._tcp.")
            return MdnsServiceKind.LegacyAdb;

        if (serviceType.Contains("adb", StringComparison.OrdinalIgnoreCase)
            && serviceType.Contains("_tcp", StringComparison.OrdinalIgnoreCase)
            && !serviceType.Contains("tls", StringComparison.OrdinalIgnoreCase))
            return MdnsServiceKind.LegacyAdb;

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopScanning();
    }

    // adb-XXX   _adb-tls-connect._tcp   192.168.1.1:37123
    // also: _adb._tcp without trailing dots; extra columns after the port
    [GeneratedRegex(
        @"^(?<name>\S+)\s+(?<type>_adb(?:-tls-(?:connect|pairing))?\.?_tcp\.?)\s+(?<ip>\d{1,3}(?:\.\d{1,3}){3}):(?<port>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MdnsLineRegex();

    [GeneratedRegex(
        @"^(?<name>\S+)\s+(?<type>\S*adb\S*_tcp\S*)\s+(?<ip>\d{1,3}(?:\.\d{1,3}){3}):(?<port>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MdnsLineLooseRegex();
}
