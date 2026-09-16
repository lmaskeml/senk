using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Core.Services;
using AndroidManager.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using Serilog;

namespace AndroidManager.Settings.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IAppDialogService _dialogs;
    private bool _loading;

    [ObservableProperty] private string _adbPath = string.Empty;
    [ObservableProperty] private string _fastbootPath = string.Empty;
    [ObservableProperty] private string _scrcpyPath = string.Empty;
    [ObservableProperty] private string _aaptPath = string.Empty;
    [ObservableProperty] private int _adbPort = 5037;

    [ObservableProperty] private AppTheme _selectedTheme = AppTheme.Dark;
    [ObservableProperty] private string _selectedAccent = "Blue";
    [ObservableProperty] private double _fontSize = 14;
    [ObservableProperty] private bool _showHidden;
    [ObservableProperty] private string _selectedLanguage = "tr-TR";

    [ObservableProperty] private string _defaultDownloadPath = string.Empty;
    [ObservableProperty] private string _defaultBackupPath = string.Empty;
    [ObservableProperty] private bool _overwriteOnTransfer;

    [ObservableProperty] private int _mirrorFps = 60;
    [ObservableProperty] private int _mirrorBitRate = 8;
    [ObservableProperty] private int _mirrorMaxSize = 1080;
    [ObservableProperty] private bool _mirrorStayAwake = true;

    [ObservableProperty] private bool _notificationsEnabled = true;
    [ObservableProperty] private bool _notifyOnDeviceConnect = true;
    [ObservableProperty] private bool _notifyOnTransferComplete = true;
    [ObservableProperty] private bool _notifyOnBackupComplete = true;

    [ObservableProperty] private bool _wirelessAutoReconnect = true;
    [ObservableProperty] private bool _wirelessMdnsDiscovery = true;
    [ObservableProperty] private bool _wirelessCompanionDiscovery = true;
    [ObservableProperty] private bool _wirelessAutoRecoverEndpoint = true;
    [ObservableProperty] private bool _wirelessShowEndpointChanges = true;
    [ObservableProperty] private bool _wirelessAutoRestartMirror = true;

    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _hasUnsavedChanges;

    public ObservableCollection<string> Languages { get; } = ["tr-TR", "en-US", "de-DE", "fr-FR", "es-ES"];
    public ObservableCollection<AppTheme> Themes { get; } = [AppTheme.Light, AppTheme.Dark, AppTheme.System];
    public ObservableCollection<ThemeAccentColor> AccentColors { get; } = new(ThemeAccents.All);

    public SettingsViewModel(ISettingsService settings, IAppDialogService dialogs)
    {
        _settings = settings;
        _dialogs = dialogs;
        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        _loading = true;
        var s = _settings.Current;

        AdbPath = s.AdbPath;
        FastbootPath = s.FastbootPath;
        ScrcpyPath = s.ScrcpyPath;
        AaptPath = s.AaptPath;
        AdbPort = s.AdbPort;

        SelectedTheme = s.Theme;
        SelectedAccent = string.IsNullOrWhiteSpace(s.AccentName) ? "Cyan" : s.AccentName;
        FontSize = s.FontSize;
        ShowHidden = s.ShowHidden;
        SelectedLanguage = s.Language;

        DefaultDownloadPath = s.DefaultDownloadPath;
        DefaultBackupPath = s.DefaultBackupPath;
        OverwriteOnTransfer = s.OverwriteOnTransfer;

        MirrorFps = s.MirrorFps;
        MirrorBitRate = s.MirrorBitRate;
        MirrorMaxSize = s.MirrorMaxSize;
        MirrorStayAwake = s.MirrorStayAwake;

        NotificationsEnabled = s.NotificationsEnabled;
        NotifyOnDeviceConnect = s.NotifyOnDeviceConnect;
        NotifyOnTransferComplete = s.NotifyOnTransferComplete;
        NotifyOnBackupComplete = s.NotifyOnBackupComplete;

        WirelessAutoReconnect = s.WirelessAutoReconnect;
        WirelessMdnsDiscovery = s.WirelessMdnsDiscovery;
        WirelessCompanionDiscovery = s.WirelessCompanionDiscovery;
        WirelessAutoRecoverEndpoint = s.WirelessAutoRecoverEndpoint;
        WirelessShowEndpointChanges = s.WirelessShowEndpointChanges;
        WirelessAutoRestartMirror = s.WirelessAutoRestartMirror;

        HasUnsavedChanges = false;
        _loading = false;
    }

    [RelayCommand]
    private void Save()
    {
        var s = _settings.Current;

        s.AdbPath = AdbPath;
        s.FastbootPath = FastbootPath;
        s.ScrcpyPath = ScrcpyPath;
        s.AaptPath = AaptPath;
        s.AdbPort = AdbPort;

        s.Theme = SelectedTheme;
        s.AccentName = SelectedAccent;
        s.FontSize = FontSize;
        s.ShowHidden = ShowHidden;
        s.Language = SelectedLanguage;

        s.DefaultDownloadPath = DefaultDownloadPath;
        s.DefaultBackupPath = DefaultBackupPath;
        s.OverwriteOnTransfer = OverwriteOnTransfer;

        s.MirrorFps = MirrorFps;
        s.MirrorBitRate = MirrorBitRate;
        s.MirrorMaxSize = MirrorMaxSize;
        s.MirrorStayAwake = MirrorStayAwake;

        s.NotificationsEnabled = NotificationsEnabled;
        s.NotifyOnDeviceConnect = NotifyOnDeviceConnect;
        s.NotifyOnTransferComplete = NotifyOnTransferComplete;
        s.NotifyOnBackupComplete = NotifyOnBackupComplete;

        s.WirelessAutoReconnect = WirelessAutoReconnect;
        s.WirelessMdnsDiscovery = WirelessMdnsDiscovery;
        s.WirelessCompanionDiscovery = WirelessCompanionDiscovery;
        s.WirelessAutoRecoverEndpoint = WirelessAutoRecoverEndpoint;
        s.WirelessShowEndpointChanges = WirelessShowEndpointChanges;
        s.WirelessAutoRestartMirror = WirelessAutoRestartMirror;

        _settings.Save();
        ThemeHelper.Apply(SelectedTheme, SelectedAccent, FontSize);

        HasUnsavedChanges = false;
        StatusMessage = "Ayarlar kaydedildi";
        Log.Information("Ayarlar kaydedildi");
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        var ok = await _dialogs.ShowConfirmationAsync(
            "Sıfırla",
            "Tüm ayarlar varsayılana sıfırlansın mı?");
        if (!ok) return;

        _settings.Reset();
        LoadFromSettings();
        ThemeHelper.Apply(SelectedTheme, SelectedAccent, FontSize);
        StatusMessage = "Varsayılan ayarlar yüklendi";
    }

    [RelayCommand]
    private async Task BrowseAdbPathAsync()
    {
        var files = await _dialogs.PickOpenFilesAsync("adb.exe seçin", "adb.exe|adb.exe|Tüm Dosyalar|*.*");
        if (files is { Count: > 0 })
            AdbPath = files[0];
    }

    [RelayCommand]
    private async Task BrowseFastbootPathAsync()
    {
        var files = await _dialogs.PickOpenFilesAsync(
            "fastboot.exe seçin",
            "fastboot.exe|fastboot.exe|Tüm Dosyalar|*.*");
        if (files is { Count: > 0 })
            FastbootPath = files[0];
    }

    [RelayCommand]
    private async Task BrowseScrcpyPathAsync()
    {
        var files = await _dialogs.PickOpenFilesAsync("scrcpy.exe seçin", "scrcpy.exe|scrcpy.exe|Tüm Dosyalar|*.*");
        if (files is { Count: > 0 })
            ScrcpyPath = files[0];
    }

    [RelayCommand]
    private async Task BrowseAaptPathAsync()
    {
        var files = await _dialogs.PickOpenFilesAsync("aapt.exe seçin", "aapt.exe|aapt.exe|Tüm Dosyalar|*.*");
        if (files is { Count: > 0 })
            AaptPath = files[0];
    }

    [RelayCommand]
    private async Task BrowseDownloadPathAsync()
    {
        var folder = await _dialogs.PickFolderAsync("İndirme klasörünü seçin");
        if (!string.IsNullOrWhiteSpace(folder))
            DefaultDownloadPath = folder;
    }

    [RelayCommand]
    private async Task BrowseBackupPathAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Yedek klasörünü seçin");
        if (!string.IsNullOrWhiteSpace(folder))
            DefaultBackupPath = folder;
    }

    [RelayCommand]
    private async Task TestAdbConnectionAsync()
    {
        StatusMessage = "ADB test ediliyor...";
        try
        {
            var path = AdbPath;
            if (!Path.IsPathRooted(path))
                path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);

            var psi = new ProcessStartInfo
            {
                FileName = path,
                Arguments = "version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)
                             ?? throw new InvalidOperationException("adb süreci başlatılamadı.");
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await using var killReg = timeoutCts.Token.Register(() => ProcessWaitHelper.TryKill(proc));
            try
            {
                var output = await proc.StandardOutput.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(true);
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(true);

                StatusMessage = output.Contains("Android Debug Bridge", StringComparison.OrdinalIgnoreCase)
                    ? $"ADB çalışıyor: {output.Split('\n')[0].Trim()}"
                    : "ADB yanıt vermedi";
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "ADB yanıt vermedi (zaman aşımı)";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"ADB bulunamadı: {ex.Message}";
        }
    }

    partial void OnAdbPathChanged(string value) => MarkDirty();
    partial void OnFastbootPathChanged(string value) => MarkDirty();
    partial void OnScrcpyPathChanged(string value) => MarkDirty();
    partial void OnAaptPathChanged(string value) => MarkDirty();
    partial void OnSelectedThemeChanged(AppTheme value)
    {
        MarkDirty();
        if (!_loading)
            ThemeHelper.Apply(value, SelectedAccent, FontSize);
    }

    partial void OnSelectedAccentChanged(string value)
    {
        MarkDirty();
        if (!_loading)
            ThemeHelper.Apply(SelectedTheme, value, FontSize);
    }

    partial void OnFontSizeChanged(double value)
    {
        MarkDirty();
        if (!_loading)
            ThemeHelper.Apply(SelectedTheme, SelectedAccent, value);
    }

    partial void OnShowHiddenChanged(bool value) => MarkDirty();
    partial void OnSelectedLanguageChanged(string value) => MarkDirty();
    partial void OnDefaultDownloadPathChanged(string value) => MarkDirty();
    partial void OnDefaultBackupPathChanged(string value) => MarkDirty();
    partial void OnOverwriteOnTransferChanged(bool value) => MarkDirty();
    partial void OnMirrorFpsChanged(int value) => MarkDirty();
    partial void OnMirrorBitRateChanged(int value) => MarkDirty();
    partial void OnMirrorMaxSizeChanged(int value) => MarkDirty();
    partial void OnMirrorStayAwakeChanged(bool value) => MarkDirty();
    partial void OnNotificationsEnabledChanged(bool value) => MarkDirty();
    partial void OnNotifyOnDeviceConnectChanged(bool value) => MarkDirty();
    partial void OnNotifyOnTransferCompleteChanged(bool value) => MarkDirty();
    partial void OnNotifyOnBackupCompleteChanged(bool value) => MarkDirty();
    partial void OnWirelessAutoReconnectChanged(bool value) => MarkDirty();
    partial void OnWirelessMdnsDiscoveryChanged(bool value) => MarkDirty();
    partial void OnWirelessCompanionDiscoveryChanged(bool value) => MarkDirty();
    partial void OnWirelessAutoRecoverEndpointChanged(bool value) => MarkDirty();
    partial void OnWirelessShowEndpointChangesChanged(bool value) => MarkDirty();
    partial void OnWirelessAutoRestartMirrorChanged(bool value) => MarkDirty();

    private void MarkDirty()
    {
        if (!_loading)
            HasUnsavedChanges = true;
    }
}

public static class ThemeHelper
{
    private const string NexusColorsMarker = "NexusColors.";

    public static void Apply(AppTheme theme, string? accentName = null, double fontSize = 14)
    {
        try
        {
            var palette = new PaletteHelper();
            var mdTheme = palette.GetTheme();
            var baseTheme = theme switch
            {
                AppTheme.Light => BaseTheme.Light,
                AppTheme.Dark => BaseTheme.Dark,
                AppTheme.System => IsSystemDark() ? BaseTheme.Dark : BaseTheme.Light,
                _ => BaseTheme.Dark
            };
            mdTheme.SetBaseTheme(baseTheme);

            var accent = ThemeAccents.All.FirstOrDefault(a =>
                string.Equals(a.MaterialName, accentName, StringComparison.OrdinalIgnoreCase))
                ?? ThemeAccents.All[0];

            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(accent.Hex)!;
            mdTheme.SetPrimaryColor(color);
            mdTheme.SetSecondaryColor(color);

            palette.SetTheme(mdTheme);

            var app = System.Windows.Application.Current;
            if (app?.Resources is null)
                return;

            app.Resources["GlobalFontSize"] = fontSize;
            SwapNexusColors(app.Resources, baseTheme == BaseTheme.Dark);
            SyncMaterialDesignInk(app.Resources);
            ApplyFontSizeToWindows(app, fontSize);
            DwmWindowFrame.ApplyAll(baseTheme == BaseTheme.Dark);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Tema uygulanamadı");
        }
    }

    private static void SwapNexusColors(System.Windows.ResourceDictionary root, bool dark)
    {
        var merged = root.MergedDictionaries;
        System.Windows.ResourceDictionary? existing = null;
        foreach (var dict in merged)
        {
            if (dict.Source?.OriginalString.Contains(NexusColorsMarker, StringComparison.OrdinalIgnoreCase) == true)
            {
                existing = dict;
                break;
            }
        }

        var uri = new Uri(
            dark
                ? "pack://application:,,,/Themes/Nexus/NexusColors.Dark.xaml"
                : "pack://application:,,,/Themes/Nexus/NexusColors.Light.xaml",
            UriKind.Absolute);

        var next = new System.Windows.ResourceDictionary { Source = uri };
        if (existing is not null)
        {
            var index = merged.IndexOf(existing);
            merged.RemoveAt(index);
            merged.Insert(index, next);
        }
        else
        {
            merged.Add(next);
        }
    }

    /// <summary>
    /// PaletteHelper.SetTheme kök kaynaklara MaterialDesign mürekkebini yazar.
    /// Karanlık Nexus zemininde okunması için Nexus metin fırçalarına eşitler.
    /// </summary>
    private static void SyncMaterialDesignInk(System.Windows.ResourceDictionary root)
    {
        var primary = ResolveBrush(root, "Nexus.TextPrimary");
        if (primary is null)
            return;

        var secondary = ResolveBrush(root, "Nexus.TextSecondary") ?? primary;
        var muted = ResolveBrush(root, "Nexus.TextMuted") ?? secondary;
        var surface = ResolveBrush(root, "Nexus.SurfaceElevated");
        var border = ResolveBrush(root, "Nexus.Border");

        void Set(string key, System.Windows.Media.Brush brush) => root[key] = brush;

        Set("MaterialDesignBody", primary);
        Set("MaterialDesignBodyLight", secondary);
        Set("MaterialDesignColumnHeader", secondary);
        Set("MaterialDesign.Brush.Foreground", primary);
        Set("MaterialDesign.Brush.Foreground.Light", secondary);
        Set("MaterialDesign.Brush.Foreground.Hint", muted);
        Set("MaterialDesign.Brush.Foreground.Disabled", muted);
        Set("MaterialDesign.Brush.Foreground.TextBox", primary);
        Set("MaterialDesign.Brush.TextBox.HoverBackground", surface ?? primary);
        if (surface is not null)
        {
            Set("MaterialDesign.Brush.Background.TextBox", surface);
            Set("MaterialDesign.Brush.ComboBox.FilledBackground", surface);
            Set("MaterialDesign.Brush.Background", surface);
        }

        if (border is not null)
        {
            Set("MaterialDesign.Brush.Foreground.TextBox.Border", border);
            Set("MaterialDesignTextBoxBorder", border);
        }
    }

    private static System.Windows.Media.Brush? ResolveBrush(System.Windows.ResourceDictionary root, string key) =>
        root[key] as System.Windows.Media.Brush;

    private static void ApplyFontSizeToWindows(System.Windows.Application app, double fontSize)
    {
        foreach (System.Windows.Window window in app.Windows)
            window.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, fontSize);
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int i && i == 0;
        }
        catch
        {
            return false;
        }
    }
}
