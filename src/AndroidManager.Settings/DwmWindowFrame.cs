using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AndroidManager.Settings;

/// <summary>Win11/10 DWM kenar ve köşe rengini Nexus temasına bağlar.</summary>
public static class DwmWindowFrame
{
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int DwmwcpRound = 2;
    private const int DwmwcpDoNotRound = 1;

    public static void ApplyAll(bool dark)
    {
        var app = Application.Current;
        if (app is null)
            return;

        foreach (Window window in app.Windows)
            Apply(window, dark);
    }

    public static void Apply(Window window) => Apply(window, IsApplicationDark());

    public static bool IsApplicationDark()
    {
        var app = Application.Current;
        if (app?.Resources.MergedDictionaries is null)
            return true;

        foreach (var dict in app.Resources.MergedDictionaries)
        {
            var source = dict.Source?.OriginalString;
            if (source is null)
                continue;
            if (source.Contains("NexusColors.Light", StringComparison.OrdinalIgnoreCase))
                return false;
            if (source.Contains("NexusColors.Dark", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return true;
    }

    public static void Apply(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (hwnd == IntPtr.Zero)
            return;

        var useDark = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref useDark, sizeof(int));

        var corner = window.WindowState == WindowState.Maximized ? DwmwcpDoNotRound : DwmwcpRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));

        var bg = ToColorRef(ResolveColor(
            window,
            "Nexus.Background.Color",
            dark ? (byte)0x0B : (byte)0xF1,
            dark ? (byte)0x0F : (byte)0xF5,
            dark ? (byte)0x14 : (byte)0xF9));
        var fg = ToColorRef(ResolveColor(
            window,
            "Nexus.TextPrimary.Color",
            dark ? (byte)0xF8 : (byte)0x0F,
            dark ? (byte)0xFA : (byte)0x17,
            dark ? (byte)0xFC : (byte)0x2A));
        var border = bg;
        _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref bg, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref fg, sizeof(int));
    }

    private static Color ResolveColor(Window window, string key, byte r, byte g, byte b)
    {
        if (window.TryFindResource(key) is Color color)
            return color;
        if (Application.Current?.TryFindResource(key) is Color appColor)
            return appColor;
        return Color.FromRgb(r, g, b);
    }

    private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
