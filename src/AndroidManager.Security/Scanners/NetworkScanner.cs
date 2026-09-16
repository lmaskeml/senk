using System.Globalization;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Security.Data;
using AndroidManager.Security.Engines;
using Serilog;

namespace AndroidManager.Security.Scanners;

public sealed class NetworkScanner
{
    private readonly IAdbService _adb;
    private readonly ThreatDatabase _db;
    private readonly HashSet<string> _staticC2Hints;

    public NetworkScanner(IAdbService adb, ThreatDatabase db, IEnumerable<string>? staticC2Hints = null)
    {
        _adb = adb;
        _db = db;
        _staticC2Hints = new HashSet<string>(staticC2Hints ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public async Task<List<ThreatItem>> ScanNetworkAsync(
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var threats = new List<ThreatItem>();
        progress?.Report(new ScanProgress { Phase = "Ağ analiz ediliyor", Percentage = 10 });

        var connections = await GetActiveConnectionsAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(new ScanProgress
        {
            Phase = "Ağ analiz ediliyor",
            CurrentItem = $"{connections.Count} bağlantı",
            Percentage = 40
        });

        foreach (var conn in connections)
        {
            var evidence = new List<ThreatEvidence>();
            var isC2 = _db.IsKnownC2(conn.RemoteHost) || _staticC2Hints.Contains(conn.RemoteHost);

            if (isC2)
            {
                evidence.Add(new ThreatEvidence
                {
                    Code = "runtime_c2",
                    Description = $"Aktif C&C bağlantısı: {conn.RemoteHost}:{conn.RemotePort}",
                    ScoreWeight = 45
                });

                if (_staticC2Hints.Contains(conn.RemoteHost))
                {
                    evidence.Add(new ThreatEvidence
                    {
                        Code = "static_runtime_correl",
                        Description = "Statik C&C + runtime korelasyonu",
                        ScoreWeight = 25
                    });
                }
            }

            if (IsSuspiciousPort(conn.RemotePort))
            {
                evidence.Add(new ThreatEvidence
                {
                    Code = "susp_port",
                    Description = $"Alışılmadık uzak port: {conn.RemotePort}",
                    ScoreWeight = 8
                });
            }

            if (evidence.Count == 0) continue;

            var item = RiskEngine.Finalize(new ThreatItem
            {
                Name = isC2
                    ? $"C&C bağlantısı: {conn.RemoteHost}"
                    : $"Şüpheli ağ: {conn.RemoteHost}:{conn.RemotePort}",
                Type = isC2 ? ThreatType.Malware : ThreatType.Suspicious,
                Category = ThreatCategory.Network,
                Description = $"{conn.RemoteHost}:{conn.RemotePort}",
                NetworkIndicators = [$"{conn.RemoteHost}:{conn.RemotePort}"],
                Evidence = evidence,
                RemediationAdvice = isC2
                    ? "Bağlantıyı yapan uygulamayı bulun ve kaldırın."
                    : "Bağlantının meşru olup olmadığını kontrol edin."
            });

            if (RiskEngine.MeetsThreatThreshold(item) || isC2)
                threats.Add(item);
        }

        progress?.Report(new ScanProgress { Phase = "Ağ analiz ediliyor", Percentage = 80 });
        threats.AddRange(await CheckDnsAsync(cancellationToken).ConfigureAwait(false));
        progress?.Report(new ScanProgress
        {
            Phase = "Ağ analiz ediliyor",
            Percentage = 100,
            ThreatsFound = threats.Count
        });

        return threats;
    }

    private async Task<List<NetworkConnection>> GetActiveConnectionsAsync(CancellationToken ct)
    {
        var connections = new List<NetworkConnection>();
        try
        {
            var raw = await _adb.ExecuteShellAsync(
                "cat /proc/net/tcp 2>/dev/null; echo ---; cat /proc/net/tcp6 2>/dev/null", ct)
                .ConfigureAwait(false);

            foreach (var line in raw.Split('\n'))
            {
                if (line.Contains("---") || line.TrimStart().StartsWith("sl", StringComparison.OrdinalIgnoreCase))
                    continue;
                var conn = ParseTcpLine(line);
                if (conn is null) continue;
                if (conn.State != "ESTABLISHED") continue;
                if (IsLocalAddress(conn.RemoteHost)) continue;
                connections.Add(conn);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Ağ bağlantıları okunamadı");
        }

        return connections;
    }

    private async Task<List<ThreatItem>> CheckDnsAsync(CancellationToken ct)
    {
        var threats = new List<ThreatItem>();
        try
        {
            var dns = await _adb.ExecuteShellAsync("getprop net.dns1; getprop net.dns2", ct)
                .ConfigureAwait(false);
            foreach (var bad in new[] { "85.25.43.95", "198.105.244.11", "94.242.200.4" })
            {
                if (!dns.Contains(bad, StringComparison.OrdinalIgnoreCase)) continue;
                threats.Add(RiskEngine.Finalize(new ThreatItem
                {
                    Name = "Şüpheli DNS sunucusu",
                    Type = ThreatType.Suspicious,
                    Severity = ThreatSeverity.High,
                    Category = ThreatCategory.Network,
                    Description = $"Kötü amaçlı DNS göstergesi: {bad}",
                    Evidence =
                    [
                        new ThreatEvidence
                        {
                            Code = "bad_dns",
                            Description = bad,
                            ScoreWeight = 35
                        }
                    ],
                    RemediationAdvice = "Wi‑Fi DNS ayarını güvenilir bir sunucuya alın."
                }));
            }
        }
        catch
        {
            // ignore
        }

        return threats;
    }

    private static NetworkConnection? ParseTcpLine(string line)
    {
        try
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) return null;
            var local = parts[1].Split(':');
            var remote = parts[2].Split(':');
            if (local.Length < 2 || remote.Length < 2) return null;

            return new NetworkConnection
            {
                LocalPort = Convert.ToInt32(local[1], 16),
                RemoteHost = HexToIp(remote[0]),
                RemotePort = Convert.ToInt32(remote[1], 16),
                State = GetTcpState(parts[3])
            };
        }
        catch
        {
            return null;
        }
    }

    private static string HexToIp(string hex)
    {
        if (hex.Length == 8)
        {
            var val = uint.Parse(hex, NumberStyles.HexNumber);
            return $"{val & 0xFF}.{(val >> 8) & 0xFF}.{(val >> 16) & 0xFF}.{(val >> 24) & 0xFF}";
        }

        return hex;
    }

    private static string GetTcpState(string hex) => hex.ToUpperInvariant() switch
    {
        "01" => "ESTABLISHED",
        "02" => "SYN_SENT",
        "0A" => "LISTEN",
        _ => "OTHER"
    };

    private static bool IsLocalAddress(string ip) =>
        ip.StartsWith("127.") || ip.StartsWith("10.") || ip.StartsWith("192.168.") ||
        ip.StartsWith("172.") || ip is "0.0.0.0" or "::" or "00000000000000000000000000000000";

    private static bool IsSuspiciousPort(int port) =>
        port is 1337 or 4444 or 5555 or 6666 or 8888 or 9999 or 31337;

    private sealed class NetworkConnection
    {
        public string RemoteHost { get; init; } = "";
        public int RemotePort { get; init; }
        public int LocalPort { get; init; }
        public string State { get; init; } = "";
    }
}
