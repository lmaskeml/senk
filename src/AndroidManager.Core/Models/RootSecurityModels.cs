using System.Text;

namespace AndroidManager.Core.Models;

public enum RootAccess
{
    None,
    Denied,
    Magisk,
    SuperSU,
    KernelSU,
    APatch,
    Other
}

public sealed class RootStatus
{
    public RootAccess Access { get; init; } = RootAccess.None;
    public bool Verified { get; init; }
    public string SuPath { get; init; } = "";
    public string MagiskVersion { get; init; } = "";
    public bool BusyboxInstalled { get; init; }
    public string Detail { get; init; } = "";

    public bool IsRooted => Verified && Access is
        RootAccess.Magisk or RootAccess.SuperSU or RootAccess.KernelSU or RootAccess.APatch or RootAccess.Other;

    public string StatusText => Access switch
    {
        RootAccess.None => "Root bulunamadı",
        RootAccess.Denied => "su mevcut ama root izni verilmedi",
        RootAccess.Magisk => Verified
            ? $"Magisk root aktif (v{MagiskVersion})"
            : $"Magisk bulundu (v{MagiskVersion}) — izin testi gerekli",
        RootAccess.SuperSU => Verified ? "SuperSU root aktif" : "SuperSU bulundu — izin testi gerekli",
        RootAccess.KernelSU => Verified ? "KernelSU root aktif" : "KernelSU bulundu — izin testi gerekli",
        RootAccess.APatch => Verified ? "APatch root aktif" : "APatch bulundu — izin testi gerekli",
        _ => Verified ? "Root erişimi aktif" : "su bulundu — izin testi gerekli"
    };

    public string StatusColor => IsRooted
        ? "#4CAF50"
        : Access == RootAccess.None ? "#F44336" : "#FF9800";
}

public sealed class MountInfo
{
    public string Device { get; init; } = "";
    public string Path { get; init; } = "";
    public string FileSystem { get; init; } = "";
    public bool IsReadOnly { get; init; } = true;

    public string ModeLabel => IsReadOnly ? "ro" : "rw";
    public string ModeColor => IsReadOnly ? "#9E9E9E" : "#FF9800";
}

public sealed class MountStatus
{
    public List<MountInfo> Mounts { get; init; } = [];

    public MountInfo? Get(string path) =>
        Mounts.FirstOrDefault(m => m.Path == path);

    public bool IsWritable(string path) =>
        Get(path) is { IsReadOnly: false };

    public bool SystemWritable => IsWritable("/system") || IsWritable("/");
    public bool VendorWritable => IsWritable("/vendor");
    public bool ProductWritable => IsWritable("/product");

    public string Summary => Mounts.Count == 0
        ? "Bölüm bilgisi alınamadı"
        : SystemWritable
            ? "Sistem bölümü yazılabilir (rw)"
            : "Sistem bölümü salt-okunur (ro)";
}

public sealed class BootSecurityInfo
{
    public string VerifiedBootState { get; init; } = "bilinmiyor";
    public bool BootloaderLocked { get; init; } = true;
    public string SelinuxMode { get; init; } = "bilinmiyor";
    public bool Debuggable { get; init; }
    public bool SecureBuild { get; init; } = true;
    public bool OemUnlockAllowed { get; init; }
    public string AndroidSdk { get; init; } = "";

    public bool SelinuxEnforcing =>
        SelinuxMode.Equals("Enforcing", StringComparison.OrdinalIgnoreCase);

    public string StatusText
    {
        get
        {
            if (VerifiedBootState is "orange" or "red")
                return $"Boot doğrulaması başarısız ({VerifiedBootState})";
            if (!SelinuxEnforcing)
                return $"SELinux: {SelinuxMode}";
            if (Debuggable)
                return "Geliştirici (debug) modu açık";
            return "Boot zinciri doğrulanmış, SELinux zorluyor";
        }
    }

    public string StatusColor =>
        VerifiedBootState is "orange" or "red" || !SelinuxEnforcing || Debuggable
            ? "#FF9800"
            : "#4CAF50";

    public string BootloaderLockedText => BootloaderLocked ? "Kilitli" : "Açık";
    public string DebuggableText => Debuggable ? "Açık" : "Kapalı";
    public string SecureBuildText => SecureBuild ? "Evet" : "Hayır";
}

public sealed class SystemScanOptions
{
    public bool ScanRecentFiles { get; init; } = true;
    public bool ScanInitScripts { get; init; } = true;
    public bool ScanMagiskModules { get; init; } = true;
    public bool ScanHosts { get; init; } = true;
    public bool ScanAccessibility { get; init; } = true;
    public bool ScanDataLocalTmp { get; init; } = true;
}

public sealed class SystemScanResult
{
    public List<ThreatItem> Threats { get; init; } = [];
    public BootSecurityInfo Boot { get; init; } = new();
    public MountStatus Mounts { get; init; } = new();
    public int ScannedChecks { get; init; }
    public TimeSpan Duration { get; init; }
    public bool RootAvailable { get; init; }

    public string Summary
    {
        get
        {
            if (!RootAvailable)
                return "Root erişimi yok — derin sistem taraması çalışamaz";

            var sb = new StringBuilder();
            sb.Append(Threats.Count == 0
                ? $"Sistem temiz ({Duration:mm\\:ss})"
                : $"{Threats.Count} sistem tehdidi ({Duration:mm\\:ss})");

            var critical = Threats.Count(t => t.Severity == ThreatSeverity.Critical);
            var high = Threats.Count(t => t.Severity == ThreatSeverity.High);
            if (critical > 0) sb.Append($" · {critical} kritik");
            if (high > 0) sb.Append($" · {high} yüksek");
            return sb.ToString();
        }
    }
}
