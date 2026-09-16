using System.IO;
using System.Windows;
using System.Windows.Threading;
using AndroidManager.Apps;
using AndroidManager.Backup;
using AndroidManager.Core;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Device;
using AndroidManager.Device.Services;
using AndroidManager.Files;
using AndroidManager.Gallery;
using AndroidManager.Messages;
using AndroidManager.Mirror;
using AndroidManager.Security;
using AndroidManager.Settings;
using AndroidManager.Transfer;
using AndroidManager.Settings.ViewModels;
using AndroidManager.Shell.Services;
using AndroidManager.Shell.ViewModels;
using AndroidManager.Shell.Views;
using Prism.DryIoc;
using Prism.Ioc;
using Prism.Modularity;
using Serilog;

namespace AndroidManager.Shell;

public partial class App : PrismApplication
{
    private bool _modulesInitialized;
    private TrayIconService? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AndroidManager",
            "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(logDir, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        ObservedTask.OnError = ex => Log.Warning(ex, "Gözlemlenmemiş görev hata verdi");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((_, e) =>
            {
                if (e.Source is Window window)
                    DwmWindowFrame.Apply(window);
            }));

        Log.Information("SeND ANDROID MANAGER başlatılıyor");
        AdbService.ApplyMdnsEnvironment();
        if (!BetaExpiryGuard.TryContinue(out var blockReason))
        {
            MessageBox.Show(
                blockReason,
                "SeND ANDROID MANAGER",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry.RegisterSingleton<MainWindow>();
        containerRegistry.RegisterSingleton<IUiDispatcher, WpfUiDispatcher>();
        containerRegistry.RegisterSingleton<IAppDialogService, AppDialogService>();
        containerRegistry.RegisterSingleton<INotificationService, NotificationService>();
        containerRegistry.RegisterSingleton<ICameraQRService, CameraQRService>();
        containerRegistry.RegisterSingleton<MainViewModel>();
        containerRegistry.Register<WifiConnectViewModel>();
        containerRegistry.RegisterForNavigation<ComingSoonView>();
    }

    protected override void ConfigureModuleCatalog(IModuleCatalog moduleCatalog)
    {
        // Settings first — other modules / shell may resolve ISettingsService.
        moduleCatalog.AddModule<SettingsModule>();
        moduleCatalog.AddModule<DeviceModule>();
        moduleCatalog.AddModule<FilesModule>();
        moduleCatalog.AddModule<AppsModule>();
        moduleCatalog.AddModule<MirrorModule>();
        moduleCatalog.AddModule<GalleryModule>();
        moduleCatalog.AddModule<MessagesModule>();
        moduleCatalog.AddModule<SecurityModule>();
        moduleCatalog.AddModule<BackupModule>();
        moduleCatalog.AddModule<TransferModule>();
    }

    protected override Window CreateShell()
    {
        EnsureModulesInitialized();
        return Container.Resolve<MainWindow>();
    }

    protected override void InitializeModules() => EnsureModulesInitialized();

    private void EnsureModulesInitialized()
    {
        if (_modulesInitialized)
            return;

        var manager = Container.Resolve<IModuleManager>();
        manager.Run();
        _modulesInitialized = true;
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        // Prism override is void — keep startup work on async Task (not async void).
        ObservedTask.Run(InitializeApplicationAsync());
    }

    private async Task InitializeApplicationAsync()
    {
        try
        {
            var settings = Container.Resolve<ISettingsService>();
            ThemeHelper.Apply(settings.Current.Theme, settings.Current.AccentName, settings.Current.FontSize);
            settings.SettingsChanged += (_, s) =>
                ThemeHelper.Apply(s.Theme, s.AccentName, s.FontSize);

            var adb = Container.Resolve<IAdbService>();
            var notifications = Container.Resolve<INotificationService>();
            adb.DeviceConnectionChanged += (_, e) =>
            {
                var name = string.IsNullOrWhiteSpace(e.Device.Model) ? e.Device.Serial : e.Device.Model;
                if (e.IsConnected)
                    notifications.ShowDeviceConnected(name);
                else
                    notifications.ShowDeviceDisconnected(name);
            };

            var watchdog = Container.Resolve<IConnectionWatchdog>();
            var persist = Container.Resolve<IConnectionPersistService>();
            var lastState = WirelessLinkState.Idle;

            watchdog.ConnectionLost += (_, _) =>
            {
                if (settings.Current.NotificationsEnabled)
                    notifications.ShowInfo("WiFi bağlantısı", "Bağlantı koptu — otomatik keşif başlatıldı…");
            };
            watchdog.ConnectionRestored += (_, ip) =>
            {
                if (settings.Current.NotificationsEnabled)
                    notifications.ShowDeviceConnected(ip);
            };
            watchdog.StatusChanged += (_, status) =>
            {
                if (!settings.Current.NotificationsEnabled || status.State == lastState)
                    return;

                lastState = status.State;
                switch (status.State)
                {
                    case WirelessLinkState.NeedsWirelessDebug:
                    case WirelessLinkState.AdbUnavailable:
                        notifications.ShowInfo("Kablosuz hata ayıklama", status.Message);
                        break;
                    case WirelessLinkState.PairingRequired:
                        notifications.ShowInfo("Eşleştirme", status.Message);
                        break;
                    case WirelessLinkState.DeviceOffline:
                        notifications.ShowInfo("Cihaz", status.Message);
                        break;
                    case WirelessLinkState.Connected when status.EndpointChanged:
                        if (settings.Current.WirelessShowEndpointChanges)
                            notifications.ShowInfo("WiFi", status.Message);
                        break;
                }
            };

            var window = Container.Resolve<MainWindow>();
            _trayIcon = new TrayIconService(adb, window);

            await adb.StartAsync().ConfigureAwait(true);

            if (settings.Current.WirelessAutoReconnect)
            {
                var last = await persist.GetLastAsync().ConfigureAwait(true);
                if (last is { AutoReconnect: true } && !string.IsNullOrWhiteSpace(last.IpAddress) && last.Port > 0)
                {
                    Log.Information("[WirelessGuardian] Startup resume {Name} {Ep}",
                        last.DisplayName, last.Endpoint);
                    try
                    {
                        if (await adb.ConnectWifiAsync(last.IpAddress, last.Port).ConfigureAwait(true))
                        {
                            watchdog.Start(new WirelessWatchTarget
                            {
                                IpAddress = last.IpAddress,
                                Port = last.Port,
                                DeviceId = last.DeviceId,
                                StableId = string.IsNullOrWhiteSpace(last.StableDeviceId)
                                    ? (string.IsNullOrWhiteSpace(last.DeviceId) ? "" : $"cid:{last.DeviceId}")
                                    : last.StableDeviceId,
                                SerialHint = last.Serial,
                                DeviceName = last.DeviceName,
                                DeviceModel = last.DeviceModel,
                                Transport = last.Transport,
                                AutoReconnect = true
                            }, intervalSeconds: 3);
                        }
                    }
                    catch (Exception resumeEx)
                    {
                        Log.Warning(resumeEx, "[WirelessGuardian] Startup resume failed");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ADB başlatılamadı");
            try
            {
                var dialogs = Container.Resolve<IAppDialogService>();
                await dialogs.ShowMessageAsync(
                    "ADB Hatası",
                    $"ADB başlatılamadı: {ex.Message}\n\nplatform-tools'u tools/adb altına koyun veya ADB_PATH ayarlayın.");
            }
            catch (Exception dialogEx)
            {
                Log.Error(dialogEx, "ADB hata diyaloğu gösterilemedi");
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            BetaExpiryGuard.Stamp();
            PumpUntilCompleted(ShutdownExternalDependenciesAsync());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Shutdown error");
        }

        Log.Information("SeND ANDROID MANAGER kapatılıyor");
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    /// <summary>
    /// Stops scrcpy, discovery loops, watchdog, then ADB server so host processes do not linger.
    /// </summary>
    private async Task ShutdownExternalDependenciesAsync()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;

        TryDisposeRegistered<ICameraQRService>(s => s.Dispose());
        TryDisposeRegistered<ICompanionDiscoveryService>(s => s.Dispose());
        TryDisposeRegistered<IWirelessDebugDiscoveryService>(s => s.Dispose());
        TryDisposeRegistered<IConnectionWatchdog>(s => s.Dispose());

        if (Container.IsRegistered<IMirrorService>())
        {
            try
            {
                await Container.Resolve<IMirrorService>().DisposeAsync()
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(3))
                    .ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                Log.Warning("Mirror DisposeAsync timed out during shutdown");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Mirror shutdown failed");
            }
        }

        if (Container.IsRegistered<IAdbService>())
        {
            try
            {
                await Container.Resolve<IAdbService>().DisposeAsync()
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(8))
                    .ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                Log.Warning("ADB DisposeAsync timed out during shutdown");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ADB shutdown failed");
            }
        }
    }

    /// <summary>
    /// WPF OnExit is void. Pump dispatcher until <paramref name="task"/> completes
    /// instead of blocking with Task.Wait / .Result.
    /// </summary>
    private void PumpUntilCompleted(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(
                static (_, state) => ((DispatcherFrame)state!).Continue = false,
                frame,
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }

        if (task.IsFaulted)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(task.Exception!.GetBaseException())
                .Throw();
    }

    private void TryDisposeRegistered<T>(Action<T> dispose)
        where T : class
    {
        try
        {
            if (!Container.IsRegistered<T>())
                return;
            dispose(Container.Resolve<T>());
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Dispose {Type} during shutdown failed", typeof(T).Name);
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // WIC missing WebP codec — avoid MessageBox spam while gallery uses ImageSharp.
        if (IsMissingImagingComponent(e.Exception))
        {
            Log.Debug(e.Exception, "Suppressed missing imaging component error");
            e.Handled = true;
            return;
        }

        Log.Fatal(e.Exception, "Dispatcher unhandled exception");
        e.Handled = true;
        try
        {
            MessageBox.Show(
                $"Beklenmeyen hata (uygulama açık kalacak):\n{e.Exception.Message}",
                "SeND ANDROID MANAGER",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
            // ignore
        }
    }

    private static bool IsMissingImagingComponent(Exception exception)
    {
        for (Exception? ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is System.Runtime.InteropServices.COMException { HResult: unchecked((int)0x88982F50) })
                return true;

            if (ex is NotSupportedException
                && ex.Message.Contains("görüntüleme bileşeni", StringComparison.OrdinalIgnoreCase))
                return true;

            if (ex.Message.Contains("0x88982F50", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            Log.Fatal(ex, "AppDomain unhandled exception");
        else
            Log.Fatal("AppDomain unhandled: {Object}", e.ExceptionObject);
        Log.CloseAndFlush();
    }
}
