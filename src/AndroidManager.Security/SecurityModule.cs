using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Core.Navigation;
using AndroidManager.Core.Services;
using AndroidManager.Security.Root;
using AndroidManager.Security.Root.Providers;
using AndroidManager.Security.Services;
using AndroidManager.Security.ViewModels;
using AndroidManager.Security.Views;
using Prism.Ioc;
using Prism.Modularity;

namespace AndroidManager.Security;

public sealed class SecurityModule : IModule
{
    public void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry.RegisterSingleton<ISecurityService, SecurityService>();
        containerRegistry.RegisterSingleton<IRootSecurityService, RootSecurityService>();
        containerRegistry.RegisterSingleton<IElevatedShellService, RootElevatedShellService>();
        containerRegistry.RegisterSingleton<IVirusTotalService, VirusTotalService>();
        containerRegistry.RegisterSingleton<IResidueCleanerService, ResidueCleanerService>();
        containerRegistry.RegisterSingleton<IMagiskModuleService, MagiskModuleService>();
        containerRegistry.RegisterSingleton<IMagiskDenyListService, MagiskDenyListService>();
        containerRegistry.RegisterSingleton<IBankRootHideService, BankRootHideService>();

        containerRegistry.RegisterSingleton<RootManager>();
        containerRegistry.RegisterSingleton<DeviceProfileAnalyzer>();
        containerRegistry.RegisterSingleton<RootPreflightService>();
        containerRegistry.RegisterSingleton<MagiskRootProvider>();
        containerRegistry.RegisterSingleton<KernelSuRootProvider>();
        containerRegistry.RegisterSingleton<ApatchRootProvider>();
        containerRegistry.RegisterSingleton<IRootAnalysisService>(c => new RootAnalysisService(
            c.Resolve<DeviceProfileAnalyzer>(),
            c.Resolve<RootPreflightService>(),
            [
                c.Resolve<MagiskRootProvider>(),
                c.Resolve<KernelSuRootProvider>(),
                c.Resolve<ApatchRootProvider>()
            ]));
        containerRegistry.RegisterSingleton<IRecoveryManagerService, RecoveryManagerService>();
        containerRegistry.RegisterSingleton<IRescueCenterService, RescueCenterService>();
        containerRegistry.RegisterSingleton<PayloadBinDumper>();
        containerRegistry.RegisterSingleton<FastbootFlashRunner>();
        containerRegistry.RegisterSingleton<ICustomRomFlashingEngine, CustomRomFlashingEngine>();
        containerRegistry.RegisterSingleton<ICustomRomWizardService, CustomRomWizardService>();
        containerRegistry.RegisterSingleton<IStockRomFlashService, StockRomFlashService>();
        containerRegistry.RegisterSingleton<IRomBackupVaultService, RomBackupVaultService>();

        containerRegistry.RegisterForNavigation<SecurityView, SecurityViewModel>(ViewNames.Security);
        containerRegistry.RegisterForNavigation<RootSecurityView, RootSecurityViewModel>(ViewNames.RootSecurity);
        containerRegistry.RegisterForNavigation<RecoveryManagerView, RecoveryManagerViewModel>(ViewNames.RecoveryManager);
        containerRegistry.RegisterForNavigation<CustomRomWizardView, CustomRomWizardViewModel>(ViewNames.CustomRomWizard);
        containerRegistry.RegisterForNavigation<StockRomFlashView, StockRomFlashViewModel>(ViewNames.StockRomFlash);
        containerRegistry.RegisterForNavigation<RomBackupVaultView, RomBackupVaultViewModel>(ViewNames.RomBackupVault);
        containerRegistry.RegisterForNavigation<RescueCenterView, RescueCenterViewModel>(ViewNames.RescueCenter);
        containerRegistry.RegisterForNavigation<VirusTotalView, VirusTotalViewModel>(ViewNames.VirusTotal);
        containerRegistry.RegisterForNavigation<ResidueCleanerView, ResidueCleanerViewModel>(ViewNames.ResidueCleaner);
    }

    public void OnInitialized(IContainerProvider containerProvider)
    {
    }
}
