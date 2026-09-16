using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace AndroidManager.Device.Services;

public static partial class ArpTableReader
{
    private static readonly Regex IpRegex = MyRegex();

    public static IReadOnlyList<string> GetActiveIpv4Hosts(string? subnetPrefix = null)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var entry in ParseArpOutput(RunArp()))
            {
                if (IsCandidate(entry, subnetPrefix))
                    hosts.Add(entry);
            }
        }
        catch
        {
            // ignore
        }

        foreach (var prefix in GetLocalSubnetPrefixes())
        {
            if (subnetPrefix is not null
                && !prefix.Equals(subnetPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                foreach (var entry in ParseArpOutput(RunArp()))
                    if (entry.StartsWith(prefix + ".", StringComparison.Ordinal))
                        hosts.Add(entry);
            }
            catch
            {
                // ignore
            }
        }

        return hosts.ToList();
    }

    public static IReadOnlyList<string> GetLocalSubnetPrefixes()
    {
        var prefixes = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up
                    || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback
                        or NetworkInterfaceType.Tunnel)
                    continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                        continue;

                    var bytes = ua.Address.GetAddressBytes();
                    if (bytes.Length != 4)
                        continue;

                    prefixes.Add($"{bytes[0]}.{bytes[1]}.{bytes[2]}");
                }
            }
        }
        catch
        {
            // ignore
        }

        return prefixes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsCandidate(string ip, string? subnetPrefix)
    {
        if (subnetPrefix is null)
            return true;

        return ip.StartsWith(subnetPrefix + ".", StringComparison.Ordinal);
    }

    private static string RunArp()
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "arp",
                Arguments = "-a",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
        };
        proc.Start();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        if (!proc.WaitForExit(5000))
        {
            AndroidManager.Core.Services.ProcessWaitHelper.TryKill(proc);
            proc.WaitForExit(2000);
        }

        return stdoutTask.Wait(2000) ? stdoutTask.GetAwaiter().GetResult() : string.Empty;
    }

    private static IEnumerable<string> ParseArpOutput(string output)
    {
        foreach (Match match in IpRegex.Matches(output))
        {
            var ip = match.Value;
            if (IPAddress.TryParse(ip, out var addr)
                && addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(addr))
                yield return ip;
        }
    }

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex MyRegex();
}
