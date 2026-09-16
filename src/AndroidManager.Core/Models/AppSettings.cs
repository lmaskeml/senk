namespace AndroidManager.Core.Models;

public enum AppTheme
{
    Light,
    Dark,
    System
}

public sealed class AppSettings
{
    public string AdbPath { get; set; } = "adb/adb.exe";
    /// <summary>Boş bırakılırsa adb ile aynı klasör, PATH ve bilinen konumlar taranır.</summary>
    public string FastbootPath { get; set; } = "";
    public string ScrcpyPath { get; set; } = "tools/scrcpy/scrcpy.exe";
    public string AaptPath { get; set; } = "tools/aapt.exe";
    public int AdbPort { get; set; } = 5037;

    public AppTheme Theme { get; set; } = AppTheme.Dark;
    /// <summary>Material Design primary accent (Cyan, Blue, Teal, …).</summary>
    public string AccentName { get; set; } = "Cyan";
    public string Language { get; set; } = "tr-TR";
    public double FontSize { get; set; } = 14;
    public bool ShowHidden { get; set; }

    public string DefaultDownloadPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "AndroidManager",
        "Downloads");

    public string DefaultBackupPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "AndroidManager",
        "Backups");

    public bool OverwriteOnTransfer { get; set; }

    public int MirrorFps { get; set; } = 60;
    public int MirrorBitRate { get; set; } = 8;
    public int MirrorMaxSize { get; set; } = 1080;
    public bool MirrorStayAwake { get; set; } = true;

    public bool NotificationsEnabled { get; set; } = true;
    public bool NotifyOnDeviceConnect { get; set; } = true;
    public bool NotifyOnTransferComplete { get; set; } = true;
    public bool NotifyOnBackupComplete { get; set; } = true;

    // Wireless Connection Guardian 2.0
    public bool WirelessAutoReconnect { get; set; } = true;
    public bool WirelessMdnsDiscovery { get; set; } = true;
    public bool WirelessCompanionDiscovery { get; set; } = true;
    public bool WirelessAutoRecoverEndpoint { get; set; } = true;
    public bool WirelessShowEndpointChanges { get; set; } = true;
    public bool WirelessAutoRestartMirror { get; set; } = true;
}
