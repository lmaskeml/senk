using AndroidManager.Core;
using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Core.Navigation;
using AndroidManager.Mirror.Services;
using AndroidManager.Mirror.ViewModels;
using AndroidManager.Mirror.Views;
using Prism.Ioc;
using Prism.Modularity;
using Serilog;

namespace AndroidManager.Mirror;

public sealed class MirrorModule : IModule
{
    public void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry.RegisterSingleton<IMirrorService, MirrorService>();
        containerRegistry.RegisterForNavigation<MirrorView, MirrorViewModel>(ViewNames.Mirror);
    }

    public void OnInitialized(IContainerProvider containerProvider)
    {
        var watchdog = containerProvider.Resolve<IConnectionWatchdog>();
        var mirror = containerProvider.Resolve<IMirrorService>();
        var settings = containerProvider.Resolve<ISettingsService>();
        var logger = Log.ForContext<MirrorModule>();

        ScrcpyOptions? lastOptions = null;
        var restartGate = 0;

        watchdog.ConnectionLost += (_, _) => lastOptions = null;

        watchdog.ConnectionRestored += (_, _) =>
        {
            if (!settings.Current.WirelessAutoRestartMirror || lastOptions is null)
                return;

            if (Interlocked.CompareExchange(ref restartGate, 1, 0) != 0)
                return;

            ObservedTask.Run(Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1500).ConfigureAwait(false);
                    if (mirror.IsStreaming)
                        await mirror.StopAsync().ConfigureAwait(false);
                    await mirror.StartScrcpyAsync(lastOptions).ConfigureAwait(false);
                    logger.Information("[Mirror] scrcpy restarted after ADB reconnect");
                }
                catch (Exception ex)
                {
                    logger.Warning(ex, "[Mirror] scrcpy auto-restart failed");
                }
                finally
                {
                    Interlocked.Exchange(ref restartGate, 0);
                }
            }));
        };

        MirrorReconnectCoordinator.Register(options => lastOptions = options);
    }
}

/// <summary>Minimal hook for MirrorViewModel to register last scrcpy options for auto-restart.</summary>
public static class MirrorReconnectCoordinator
{
    private static Action<ScrcpyOptions>? _register;

    public static void Register(Action<ScrcpyOptions> register) => _register = register;

    public static void NotifyStarted(ScrcpyOptions options) => _register?.Invoke(options);
}
