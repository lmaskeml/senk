using System.Collections.ObjectModel;
using AndroidManager.Gallery.ViewModels;

namespace AndroidManager.Gallery.ViewModels;

public sealed class GalleryTimelineGroup
{
    public GalleryTimelineGroup(string title, IEnumerable<GalleryItemViewModel> items)
    {
        Title = title;
        Items = new ObservableCollection<GalleryItemViewModel>(items);
    }

    public string Title { get; }
    public ObservableCollection<GalleryItemViewModel> Items { get; }
    public int Count => Items.Count;
}
