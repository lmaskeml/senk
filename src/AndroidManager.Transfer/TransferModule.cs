using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;
using AndroidManager.Core.Services;
using AndroidManager.Transfer.Services;
using AndroidManager.Transfer.ViewModels;
using AndroidManager.Transfer.Views;
using AndroidManager.Core.Navigation;
using Prism.Ioc;
using Prism.Modularity;

namespace AndroidManager.Transfer;

public sealed class TransferModule : IModule
{
    public void RegisterTypes(IContainerRegistry containerRegistry)
    {
        if (!containerRegistry.IsRegistered<IElevatedShellService>())
            containerRegistry.RegisterSingleton<IElevatedShellService, NullElevatedShellService>();

        containerRegistry.RegisterSingleton<AndroidBackupArchiveReader>();
        containerRegistry.RegisterSingleton<WhatsAppApkDowngrader>();
        containerRegistry.RegisterSingleton<AndroidWhatsAppExtractor>();
        containerRegistry.RegisterSingleton<WhatsAppSchemaConverter>();
        containerRegistry.RegisterSingleton<IosBackupManipulator>();
        containerRegistry.RegisterSingleton<IIosDeviceService, IosDeviceService>();
        containerRegistry.RegisterSingleton<IWhatsAppTransferService, WhatsAppTransferService>();
        containerRegistry.RegisterForNavigation<TransferWizardView, TransferWizardViewModel>(ViewNames.WhatsAppTransfer);
    }

    public void OnInitialized(IContainerProvider containerProvider)
    {
    }
}
