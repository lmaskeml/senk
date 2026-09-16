using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Events;
using Serilog;

namespace AndroidManager.Shell.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _trayIcon;
    private readonly IAdbService _adb;
    private readonly Window _mainWindow;

    public TrayIconService(IAdbService adb, Window mainWindow)
    {
        _adb = adb;
        _mainWindow = mainWindow;

        _trayIcon = new NotifyIcon
        {
            Text = "SeND ANDROID MANAGER",
            Visible = true,
            Icon = LoadIcon()
        };

        BuildContextMenu();
        _trayIcon.DoubleClick += (_, _) => ShowWindow();
        _adb.DeviceConnectionChanged += OnDeviceConnectionChanged;
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("SeND ANDROID MANAGER'ı Aç", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Cihazlar", null, async (_, _) =>
        {
            try
            {
                var devices = await _adb.GetDevicesAsync();
                var msg = devices.Count == 0
                    ? "Bağlı cihaz yok"
                    : string.Join(Environment.NewLine, devices.Select(d => $"• {d.Model} ({d.Serial})"));
                System.Windows.Forms.MessageBox.Show(
                    msg,
                    "Bağlı Cihazlar",
                    MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Tray cihaz listesi alınamadı");
            }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) =>
        {
            _trayIcon.Visible = false;
            Application.Current.Shutdown();
        });
        _trayIcon.ContextMenuStrip = menu;
    }

    private void OnDeviceConnectionChanged(object? sender, DeviceConnectionChangedEventArgs e)
    {
        var text = e.IsConnected
            ? $"{e.Device.Model} bağlı"
            : "SeND ANDROID MANAGER";
        UpdateTrayText(text);
    }

    private void ShowWindow()
    {
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void UpdateTrayText(string text) =>
        _trayIcon.Text = text.Length > 63 ? text[..63] : text;

    private static Icon LoadIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute));
            if (resource?.Stream is not null)
            {
                using (resource.Stream)
                using (var ico = new Icon(resource.Stream))
                    return (Icon)ico.Clone();
            }
        }
        catch
        {
            // fall through
        }

        try
        {
            var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico");
            return File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    public void Dispose()
    {
        _adb.DeviceConnectionChanged -= OnDeviceConnectionChanged;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
    }
}
