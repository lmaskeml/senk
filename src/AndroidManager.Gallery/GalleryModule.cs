using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Navigation;
using AndroidManager.Gallery.Services;
using AndroidManager.Gallery.ViewModels;
using AndroidManager.Gallery.Views;
using Prism.Ioc;
using Prism.Modularity;

namespace AndroidManager.Gallery;

public sealed class GalleryModule : IModule
{
    public void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry.RegisterSingleton<IGalleryService, GalleryService>();
        containerRegistry.RegisterSingleton<IThumbnailCache, ThumbnailCache>();
        containerRegistry.RegisterForNavigation<GalleryView, GalleryViewModel>(ViewNames.Gallery);
    }

    public void OnInitialized(IContainerProvider containerProvider)
    {
    }
}
