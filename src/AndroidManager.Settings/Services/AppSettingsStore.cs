using System.IO;
using System.Text.Json;
using AndroidManager.Core.Models;
using Serilog;

namespace AndroidManager.Settings.Services;

public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AndroidManager",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return CreateDefault();

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? CreateDefault();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Ayarlar yüklenemedi, varsayılan kullanılıyor");
            return CreateDefault();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
        Log.Information("Ayarlar kaydedildi");
    }

    public static AppSettings CreateDefault()
    {
        var settings = new AppSettings();
        Directory.CreateDirectory(settings.DefaultDownloadPath);
        Directory.CreateDirectory(settings.DefaultBackupPath);
        return settings;
    }

    public static void Reset(AppSettings target)
    {
        var def = CreateDefault();
        target.AdbPath = def.AdbPath;
        target.FastbootPath = def.FastbootPath;
        target.ScrcpyPath = def.ScrcpyPath;
        target.AaptPath = def.AaptPath;
        target.AdbPort = def.AdbPort;
        target.Theme = def.Theme;
        target.Language = def.Language;
        target.FontSize = def.FontSize;
        target.ShowHidden = def.ShowHidden;
        target.DefaultDownloadPath = def.DefaultDownloadPath;
        target.DefaultBackupPath = def.DefaultBackupPath;
        target.OverwriteOnTransfer = def.OverwriteOnTransfer;
        target.MirrorFps = def.MirrorFps;
        target.MirrorBitRate = def.MirrorBitRate;
        target.MirrorMaxSize = def.MirrorMaxSize;
        target.MirrorStayAwake = def.MirrorStayAwake;
        target.NotificationsEnabled = def.NotificationsEnabled;
        target.NotifyOnDeviceConnect = def.NotifyOnDeviceConnect;
        target.NotifyOnTransferComplete = def.NotifyOnTransferComplete;
        target.NotifyOnBackupComplete = def.NotifyOnBackupComplete;
        Save(target);
    }
}
