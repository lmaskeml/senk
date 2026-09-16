using System.IO;
using System.Windows;
using System.Windows.Controls;
using AndroidManager.Core.Abstractions;
using AndroidManager.Messages.Services;
using AndroidManager.Messages.ViewModels;
using Microsoft.Web.WebView2.Core;
using Prism.Navigation;
using Prism.Navigation.Regions;
using Serilog;

namespace AndroidManager.Messages.Views;

public partial class WhatsAppWebView : UserControl, INavigationAware, IRegionMemberLifetime
{
    private readonly IWhatsAppSessionService _session;
    private readonly IAppDialogService _dialogs;
    private bool _initialized;
    private WhatsAppWebViewModel? _vm;

    public WhatsAppWebView(IWhatsAppSessionService session, IAppDialogService dialogs)
    {
        _session = session;
        _dialogs = dialogs;
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    public bool KeepAlive => true;

    public void OnNavigatedTo(NavigationContext navigationContext)
    {
    }

    public bool IsNavigationTarget(NavigationContext navigationContext) => true;

    public void OnNavigatedFrom(NavigationContext navigationContext)
    {
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.ReloadRequested -= ReloadAsync;
            _vm.ClearSessionRequested -= ClearSessionAsync;
            _vm.ZoomRequested -= ApplyZoom;
        }

        _vm = DataContext as WhatsAppWebViewModel;
        if (_vm is null) return;

        _vm.ReloadRequested += ReloadAsync;
        _vm.ClearSessionRequested += ClearSessionAsync;
        _vm.ZoomRequested += ApplyZoom;
        _vm.HasSession = _session.HasSession;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        await EnsureWebViewAsync(navigate: true);
    }

    private async Task EnsureWebViewAsync(bool navigate)
    {
        try
        {
            _vm?.MarkLoading("WhatsApp Web motoru başlatılıyor…");

            var userData = _session.UserDataFolder;
            Directory.CreateDirectory(userData);

            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userData);
            await WebView.EnsureCoreWebView2Async(env);

            if (WebView.CoreWebView2 is null)
            {
                _vm?.MarkError("WebView2 başlatılamadı. Edge WebView2 Runtime kurulu mu?");
                return;
            }

            await _session.InitializeAsync(WebView.CoreWebView2);

            WebView.CoreWebView2.NavigationStarting -= OnNavigationStarting;
            WebView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            WebView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            WebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

            _initialized = true;
            ApplyZoom(_vm?.ZoomFactor ?? 1.0);

            if (navigate)
                WebView.CoreWebView2.Navigate(WhatsAppWebPaths.WebUrl);
            else
                _vm?.MarkReady(_session.HasSession);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WhatsApp WebView2 init failed");
            _vm?.MarkError(
                "WebView2 hatası: " + ex.Message +
                " — Microsoft Edge WebView2 Runtime gerekli olabilir.");
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        _vm?.MarkLoading("Yükleniyor…");
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            _vm?.MarkError($"Sayfa yüklenemedi ({e.WebErrorStatus}).");
            return;
        }

        if (WebView.CoreWebView2 is null)
        {
            _vm?.MarkReady(_session.HasSession);
            return;
        }

        await _session.InjectSessionKeeperAsync(WebView.CoreWebView2);
        await Task.Delay(1500);
        var info = await _session.GetSessionInfoAsync(WebView.CoreWebView2);
        _vm?.MarkReady(_session.HasSession, info);
    }

    private void ApplyZoom(double factor)
    {
        try
        {
            WebView.ZoomFactor = factor;
        }
        catch
        {
            // ignore until ready
        }
    }

    private async Task ReloadAsync()
    {
        try
        {
            if (WebView.CoreWebView2 is null)
            {
                await EnsureWebViewAsync(navigate: true);
                return;
            }

            _vm?.MarkLoading("Yenileniyor…");
            WebView.CoreWebView2.Navigate(WhatsAppWebPaths.WebUrl);
        }
        catch (Exception ex)
        {
            _vm?.MarkError(ex.Message);
        }
    }

    private async Task ClearSessionAsync()
    {
        try
        {
            var confirm = await _dialogs.ShowConfirmationAsync(
                "Oturumu Temizle",
                "WhatsApp oturumu temizlensin mi?\nTekrar QR okutmanız gerekecek.");
            if (!confirm)
                return;

            if (WebView.CoreWebView2 is null)
                await EnsureWebViewAsync(navigate: false);

            if (WebView.CoreWebView2 is null)
            {
                _vm?.MarkError("WebView2 hazır değil.");
                return;
            }

            _vm?.MarkLoading("Oturum siliniyor…");
            await _session.ClearSessionAsync(WebView.CoreWebView2);
            WebView.CoreWebView2.Navigate(WhatsAppWebPaths.WebUrl);
            if (_vm is not null)
            {
                _vm.HasSession = false;
                _vm.SessionInfo = string.Empty;
                _vm.StatusMessage = "Oturum silindi — QR ile yeniden bağlanın";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Clear WhatsApp session failed");
            _vm?.MarkError("Oturum silinemedi: " + ex.Message);
        }
    }
}
