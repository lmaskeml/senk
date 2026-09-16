using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AndroidManager.Mirror.ViewModels;

namespace AndroidManager.Mirror.Views;

public partial class MirrorView : UserControl
{
    public MirrorView()
    {
        InitializeComponent();
    }

    private async void MirrorImage_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MirrorViewModel vm || sender is not Image image)
            return;

        var point = e.GetPosition(image);
        await vm.HandleImageClickAsync(point, new Size(image.ActualWidth, image.ActualHeight));
    }
}
