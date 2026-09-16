using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Navigation;
using AndroidManager.Core.Services;
using AndroidManager.Device.Data;
using AndroidManager.Device.Services;
using AndroidManager.Device.ViewModels;
using AndroidManager.Device.Views;
using Prism.Ioc;
using Prism.Modularity;
using Prism.Navigation.Regions;

namespace AndroidManager.Device;

public sealed class DeviceModule : IModule
{
    public void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry.RegisterSingleton<AdbService>();
        containerRegistry.RegisterSingleton<IAdbService>(c => c.Resolve<AdbService>());
        containerRegistry.RegisterSingleton<IAdbSyncService, AdbSyncService>();
        containerRegistry.RegisterSingleton<IDeviceInfoService, DeviceInfoService>();
        containerRegistry.RegisterSingleton<IDeviceToolsService, DeviceToolsService>();
        containerRegistry.RegisterSingleton<IAdbTerminalService, AdbTerminalService>();
        containerRegistry.RegisterSingleton<ILogcatService, LogcatService>();
        containerRegistry.RegisterSingleton<IBootControlService, BootControlService>();
        containerRegistry.RegisterSingleton<IDiagnosticReportService, DiagnosticReportService>();

        containerRegistry.RegisterSingleton<IDevicePresenceStore, DevicePresenceStore>();
        containerRegistry.RegisterSingleton<LanProbeStrategy>();
        containerRegistry.RegisterSingleton<IRootWirelessDebugService>(c =>
        {
            IElevatedShellService? elevated = null;
            try
            {
                elevated = c.Resolve<IElevatedShellService>();
            }
            catch
            {
                // Security module not loaded yet in some test hosts.
            }

            return new RootWirelessDebugService(c.Resolve<IAdbService>(), elevated);
        });
        containerRegistry.RegisterSingleton<IWirelessDiscoveryChain, WirelessDiscoveryChain>();
        containerRegistry.RegisterSingleton<IUsbConnectionMonitor, UsbConnectionMonitor>();
        containerRegistry.RegisterSingleton<IWirelessConnectionOrchestrator, WirelessConnectionOrchestrator>();
        containerRegistry.RegisterSingleton<IWirelessDebugRecoveryService, WirelessDebugRecoveryService>();
        containerRegistry.RegisterSingleton<ICompanionBatteryGuard>(c =>
        {
            IElevatedShellService? elevated = null;
            try
            {
                elevated = c.Resolve<IElevatedShellService>();
            }
            catch
            {
                // Security module optional in test hosts.
            }

            return new CompanionBatteryGuardService(c.Resolve<IAdbService>(), elevated);
        });
        containerRegistry.RegisterSingleton<IAdvancedNetworkDiagnosticsService, AdvancedNetworkDiagnosticsService>();

        containerRegistry.RegisterSingleton<ICompanionDiscoveryService, CompanionDiscoveryService>();
        containerRegistry.RegisterSingleton<IWirelessDebugDiscoveryService, WirelessDebugDiscoveryService>();
        containerRegistry.RegisterSingleton<IDeviceIdentityService, DeviceIdentityService>();
        containerRegistry.RegisterSingleton<IDevicePresenceService, DevicePresenceService>();
        containerRegistry.RegisterSingleton<IPairingCredentialStore, PairingCredentialStore>();
        containerRegistry.RegisterSingleton<IWirelessEndpointResolver, WirelessEndpointResolver>();
        containerRegistry.RegisterSingleton<IConnectionWatchdog, ConnectionWatchdog>();
        containerRegistry.RegisterSingleton<IConnectionPersistService, ConnectionPersistService>();
        containerRegistry.RegisterSingleton<IPlatformRepository, PlatformRepository>();
        containerRegistry.RegisterSingleton<IFastbootDiscoveryService, FastbootDiscoveryService>();
        containerRegistry.RegisterForNavigation<DeviceInfoView, DeviceInfoViewModel>(ViewNames.DeviceInfo);
        containerRegistry.RegisterForNavigation<TerminalView, TerminalViewModel>(ViewNames.Terminal);
        containerRegistry.RegisterForNavigation<LogcatView, LogcatViewModel>(ViewNames.Logcat);
        containerRegistry.RegisterForNavigation<BootControlView, BootControlViewModel>(ViewNames.BootControl);
    }

    public void OnInitialized(IContainerProvider containerProvider)
    {
        var regionManager = containerProvider.Resolve<IRegionManager>();
        regionManager.RequestNavigate(RegionNames.ContentRegion, ViewNames.DeviceInfo);
    }
}
