using System.IO;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace AndroidManager.Messages.Services;

public interface IWhatsAppSessionService
{
    bool HasSession { get; }
    string UserDataFolder { get; }
    Task InitializeAsync(CoreWebView2 webView);
    Task ClearSessionAsync(CoreWebView2 webView);
    Task<string> GetSessionInfoAsync(CoreWebView2 webView);
    Task InjectSessionKeeperAsync(CoreWebView2 webView);
}

public sealed class WhatsAppSessionService : IWhatsAppSessionService
{
    public string UserDataFolder => WhatsAppWebPaths.UserDataDirectory;

    public bool HasSession
    {
        get
        {
            try
            {
                if (!Directory.Exists(UserDataFolder))
                    return false;

                return Directory.EnumerateFiles(UserDataFolder, "*", SearchOption.AllDirectories)
                    .Any(f =>
                    {
                        var name = Path.GetFileName(f);
                        return name.Contains("Cookie", StringComparison.OrdinalIgnoreCase)
                               || name.Contains("Local Storage", StringComparison.OrdinalIgnoreCase)
                               || name.EndsWith(".ldb", StringComparison.OrdinalIgnoreCase);
                    });
            }
            catch
            {
                return false;
            }
        }
    }

    public Task InitializeAsync(CoreWebView2 webView)
    {
        ArgumentNullException.ThrowIfNull(webView);

        webView.Settings.IsPasswordAutosaveEnabled = true;
        webView.Settings.IsGeneralAutofillEnabled = true;
        webView.Settings.AreDefaultContextMenusEnabled = true;
        webView.Settings.AreDevToolsEnabled = false;
        webView.Settings.IsStatusBarEnabled = false;
        webView.Settings.UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/122.0.0.0 Safari/537.36";

        webView.PermissionRequested -= OnPermissionRequested;
        webView.PermissionRequested += OnPermissionRequested;

        webView.NewWindowRequested -= OnNewWindowRequested;
        webView.NewWindowRequested += OnNewWindowRequested;

        Log.Information("WhatsApp Web session ready. HasSession={Has}", HasSession);
        return Task.CompletedTask;
    }

    public async Task ClearSessionAsync(CoreWebView2 webView)
    {
        ArgumentNullException.ThrowIfNull(webView);
        await webView.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile)
            .ConfigureAwait(true);
        Log.Information("WhatsApp Web session cleared");
    }

    public async Task InjectSessionKeeperAsync(CoreWebView2 webView)
    {
        try
        {
            await webView.ExecuteScriptAsync(
                """
                (function() {
                    if (window.__amSessionKeeper) return;
                    window.__amSessionKeeper = setInterval(function() {
                        try {
                            localStorage.setItem('am_lastActive', Date.now().toString());
                        } catch (e) {}
                    }, 30000);
                })();
                """)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "WhatsApp session keeper inject failed");
        }
    }

    public async Task<string> GetSessionInfoAsync(CoreWebView2 webView)
    {
        try
        {
            var raw = await webView.ExecuteScriptAsync(
                    """
                    (function() {
                        try {
                            var last = localStorage.getItem('am_lastActive') || '';
                            var wid = localStorage.getItem('last-wid-md')
                                || localStorage.getItem('last-wid')
                                || '';
                            return JSON.stringify({
                                hasWid: !!wid,
                                lastActive: last
                            });
                        } catch (e) {
                            return JSON.stringify({ hasWid: false, lastActive: '' });
                        }
                    })();
                    """)
                .ConfigureAwait(true);

            // ExecuteScriptAsync returns a JSON-encoded string.
            if (string.IsNullOrWhiteSpace(raw) || raw == "null")
                return HasSession ? "Oturum önbelleği mevcut" : "Oturum yok";

            var unquoted = System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? raw;
            using var doc = System.Text.Json.JsonDocument.Parse(unquoted);
            var root = doc.RootElement;
            var hasWid = root.TryGetProperty("hasWid", out var hw) && hw.GetBoolean();
            return hasWid || HasSession
                ? "Oturum aktif (kalıcı profil)"
                : "QR bekleniyor";
        }
        catch
        {
            return HasSession ? "Oturum önbelleği mevcut" : "Oturum bilgisi yok";
        }
    }

    private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (e.PermissionKind == CoreWebView2PermissionKind.Notifications)
            e.State = CoreWebView2PermissionState.Allow;
    }

    private static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (sender is CoreWebView2 wv && !string.IsNullOrWhiteSpace(e.Uri))
            wv.Navigate(e.Uri);
    }
}
