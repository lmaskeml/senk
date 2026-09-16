using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Navigation;
using AndroidManager.Messages.Services;
using AndroidManager.Messages.ViewModels;
using AndroidManager.Messages.Views;
using Prism.Ioc;
using Prism.Modularity;

namespace AndroidManager.Messages;

public sealed class MessagesModule : IModule
{
    public void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry.RegisterSingleton<ISmsService, SmsService>();
        containerRegistry.RegisterSingleton<IContactsService, ContactsService>();
        containerRegistry.RegisterSingleton<ISmsComposeBridge, SmsComposeBridge>();
        containerRegistry.RegisterSingleton<ICallLogService, CallLogService>();
        containerRegistry.RegisterSingleton<IWhatsAppSessionService, WhatsAppSessionService>();
        containerRegistry.RegisterForNavigation<SmsView, SmsViewModel>(ViewNames.Sms);
        containerRegistry.RegisterForNavigation<ContactsView, ContactsViewModel>(ViewNames.Contacts);
        containerRegistry.RegisterForNavigation<CallLogView, CallLogViewModel>(ViewNames.CallLog);
        containerRegistry.RegisterForNavigation<WhatsAppWebView, WhatsAppWebViewModel>(ViewNames.WhatsAppWeb);
    }

    public void OnInitialized(IContainerProvider containerProvider)
    {
    }
}
