using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AndroidManager.Gallery.ViewModels;
using Prism.Navigation.Regions;

namespace AndroidManager.Gallery.Views;

public partial class GalleryView : UserControl, IRegionMemberLifetime
{
    // Recreate on each visit so Dispose can unsubscribe AdbService events without leaking.
    public bool KeepAlive => false;

    public GalleryView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is GalleryViewModel vm)
            await vm.InitializeAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is IDisposable disposable)
            disposable.Dispose();
    }

    private void MediaList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is GalleryViewModel vm && vm.SelectedItem is not null)
            vm.OpenPreviewCommand.Execute(vm.SelectedItem);
    }

    private void MediaList_OnRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        // VirtualizingWrapPanel creates containers as they enter view — request thumbnails then.
        if (e.TargetObject is FrameworkElement { DataContext: GalleryItemViewModel item })
            item.EnsureThumbnailRequested();
    }

    private void MediaItem_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GalleryItemViewModel item })
            item.EnsureThumbnailRequested();
    }

    private void TimelineItem_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GalleryItemViewModel item })
            return;
        if (DataContext is not GalleryViewModel vm)
            return;

        vm.SelectedItem = item;
        if (e.ClickCount >= 2)
            vm.OpenPreviewCommand.Execute(item);
    }
}
