using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Security.Root;

public sealed class RootManager
{
    private readonly IAdbService _adb;
    private readonly ILogger _logger;
    private RootStatus? _cached;

    public RootShell Shell { get; }

    public RootManager(IAdbService adb, ILogger? logger = null)
    {
        _adb = adb;
        _logger = logger ?? Log.ForContext<RootManager>();
        Shell = new RootShell(adb, _logger);
        _adb.SelectedDeviceChanged += OnSelectedDeviceChanged;
    }

    public async Task<RootStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        _cached ??= await DetectAsync(cancellationToken).ConfigureAwait(false);
        return _cached;
    }

    public void InvalidateCache()
    {
        _cached = null;
        Shell.Reset();
    }

    public async Task<bool> EnsureActiveAsync(CancellationToken cancellationToken = default)
    {
        var st = await DetectAsync(cancellationToken).ConfigureAwait(false);
        if (st.Verified)
        {
            _cached = st;
            return true;
        }

        if (st.Access == RootAccess.None)
        {
            var okDirect = await Shell.TestRootAsync(cancellationToken).ConfigureAwait(false);
            if (okDirect)
            {
                _cached = new RootStatus
                {
                    Access = RootAccess.Other,
                    Verified = true,
                    SuPath = st.SuPath,
                    MagiskVersion = st.MagiskVersion,
                    BusyboxInstalled = st.BusyboxInstalled,
                    Detail = JoinDetail(st.Detail, "ADB kabuğu uid=0 (root)")
                };
                _logger.Information("Root testi: ADB shell uid=0");
                return true;
            }

            _cached = st;
            return false;
        }

        var ok = await Shell.TestRootAsync(cancellationToken).ConfigureAwait(false);
        _cached = new RootStatus
        {
            Access = ok ? st.Access : RootAccess.Denied,
            Verified = ok,
            SuPath = st.SuPath,
            MagiskVersion = st.MagiskVersion,
            BusyboxInstalled = st.BusyboxInstalled,
            Detail = ok ? st.Detail : "su bulundu ama cihazda root izni verilmedi"
        };

        _logger.Information("Root testi: {Ok} ({Access})", ok, _cached.Access);
        return ok;
    }

    public async Task<RootStatus> DetectAsync(CancellationToken cancellationToken = default)
    {
        var access = RootAccess.None;
        var suPath = await Shell.FindSuAsync(cancellationToken).ConfigureAwait(false) ?? "";
        var magisk = "";
        var busybox = false;
        var detail = new List<string>();
        var shellRoot = await Shell.IsAdbShellRootAsync(cancellationToken).ConfigureAwait(false);

        if (shellRoot)
            detail.Add("ADB kabuğu uid=0 (root)");

        if (!string.IsNullOrEmpty(suPath))
            detail.Add($"su: {suPath}");

        try
        {
            var v = (await _adb.ExecuteShellAsync(
                "magisk -v 2>/dev/null || magisk --version 2>/dev/null || echo NO",
                cancellationToken).ConfigureAwait(false)).Trim();
            if (v is not "NO" && !string.IsNullOrWhiteSpace(v))
            {
                magisk = v;
                access = RootAccess.Magisk;
                detail.Add($"Magisk v{v}");
            }
            else
            {
                var viaSu = (await Shell.RunRootCommandAsync(
                    "magisk -v 2>/dev/null || magisk --version 2>/dev/null || echo NO",
                    TimeSpan.FromSeconds(15),
                    cancellationToken).ConfigureAwait(false)).Trim();
                if (viaSu is not "NO" && !string.IsNullOrWhiteSpace(viaSu) &&
                    !viaSu.Contains("not found", StringComparison.OrdinalIgnoreCase))
                {
                    magisk = viaSu.Split('\n')[0].Trim();
                    access = RootAccess.Magisk;
                    detail.Add($"Magisk v{magisk} (su — uygulama gizlenmiş olabilir)");
                }
            }

            if (access == RootAccess.None)
            {
                var pkg = await _adb.ExecuteShellAsync(
                    "pm list packages 2>/dev/null | grep -iE 'magisk|topjohnwu|huskydg|vvb2060'",
                    cancellationToken).ConfigureAwait(false);
                if (pkg.Contains("magisk", StringComparison.OrdinalIgnoreCase) ||
                    pkg.Contains("topjohnwu", StringComparison.OrdinalIgnoreCase) ||
                    pkg.Contains("huskydg", StringComparison.OrdinalIgnoreCase) ||
                    pkg.Contains("vvb2060", StringComparison.OrdinalIgnoreCase))
                {
                    access = RootAccess.Magisk;
                    magisk = "paket kurulu";
                    detail.Add("Magisk paketi kurulu");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Magisk tespit hatası");
        }

        if (access == RootAccess.None)
        {
            try
            {
                var ksu = await _adb.ExecuteShellAsync(
                    "pm list packages 2>/dev/null | grep -iE 'kernelsu|ksunext|weishu'; ls /data/adb/ksu /data/adb/ksud 2>/dev/null",
                    cancellationToken).ConfigureAwait(false);
                if (ksu.Contains("kernelsu", StringComparison.OrdinalIgnoreCase) ||
                    ksu.Contains("ksunext", StringComparison.OrdinalIgnoreCase) ||
                    ksu.Contains("weishu", StringComparison.OrdinalIgnoreCase) ||
                    ksu.Contains("/ksu", StringComparison.OrdinalIgnoreCase) ||
                    ksu.Contains("ksud", StringComparison.OrdinalIgnoreCase))
                {
                    access = RootAccess.KernelSU;
                    detail.Add("KernelSU bulundu");
                }
            }
            catch
            {
                // yok
            }
        }

        if (access == RootAccess.None)
        {
            try
            {
                var ap = await _adb.ExecuteShellAsync(
                    "pm list packages me.bmax.apatch 2>/dev/null; ls /data/adb/ap 2>/dev/null",
                    cancellationToken).ConfigureAwait(false);
                if (ap.Contains("me.bmax.apatch", StringComparison.Ordinal) ||
                    ap.Contains("/ap", StringComparison.OrdinalIgnoreCase))
                {
                    access = RootAccess.APatch;
                    detail.Add("APatch bulundu");
                }
            }
            catch
            {
                // yok
            }
        }

        if (access == RootAccess.None && !string.IsNullOrEmpty(suPath))
        {
            try
            {
                var pkg = await _adb.ExecuteShellAsync(
                    "pm list packages 2>/dev/null | grep -iE 'supersu|koushik'", cancellationToken)
                    .ConfigureAwait(false);
                if (pkg.Trim().Length > 0)
                {
                    access = RootAccess.SuperSU;
                    detail.Add("SuperSU yöneticisi kurulu");
                }
            }
            catch
            {
                // yok
            }
        }

        if (access == RootAccess.None)
        {
            try
            {
                var hidden = await _adb.ExecuteShellAsync(
                    "ls /debug_ramdisk/.magisk /data/adb/magisk 2>&1",
                    cancellationToken).ConfigureAwait(false);
                if (LooksPresent(hidden))
                {
                    access = RootAccess.Magisk;
                    magisk = string.IsNullOrWhiteSpace(magisk) ? "gizli" : magisk;
                    detail.Add("Magisk izi (/debug_ramdisk veya /data/adb)");
                }
            }
            catch
            {
                // yok
            }
        }

        if (access == RootAccess.None && !string.IsNullOrEmpty(suPath))
        {
            access = RootAccess.Other;
            detail.Add("Bilinmeyen root yöntemi (su mevcut)");
        }

        if (access == RootAccess.None && shellRoot)
        {
            access = RootAccess.Other;
            detail.Add("ADB zaten root (su binary yok — userdebug/eng)");
        }

        try
        {
            var bb = (await _adb.ExecuteShellAsync(
                "busybox --list 2>/dev/null | head -1", cancellationToken).ConfigureAwait(false)).Trim();
            busybox = !string.IsNullOrWhiteSpace(bb);
            if (busybox) detail.Add("busybox mevcut");
        }
        catch
        {
            // yok
        }

        if (access == RootAccess.None)
            detail.Add("su binary bulunamadı — cihaz root'lu görünmüyor");

        return new RootStatus
        {
            Access = access,
            Verified = shellRoot && access != RootAccess.None,
            SuPath = suPath,
            MagiskVersion = magisk,
            BusyboxInstalled = busybox,
            Detail = string.Join(" · ", detail)
        };
    }

    private void OnSelectedDeviceChanged(object? sender, ConnectedDevice? device)
    {
        InvalidateCache();
        _logger.Debug("Root önbelleği temizlendi (cihaz: {Serial})", device?.Serial ?? "-");
    }

    private static bool LooksPresent(string lsOutput)
    {
        foreach (var line in lsOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (t.Contains("No such file", StringComparison.OrdinalIgnoreCase))
                continue;
            if (t.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!t.StartsWith("ls:", StringComparison.OrdinalIgnoreCase) && t.Length > 0)
                return true;
        }

        return false;
    }

    private static string JoinDetail(string existing, string extra) =>
        string.IsNullOrWhiteSpace(existing) ? extra : existing + " · " + extra;
}
