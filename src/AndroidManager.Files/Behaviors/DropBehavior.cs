using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AndroidManager.Files.ViewModels;
using Microsoft.Xaml.Behaviors;

namespace AndroidManager.Files.Behaviors;

public sealed class AndroidDragBehavior : Behavior<ListView>
{
    private Point _startPoint;

    protected override void OnAttached()
    {
        AssociatedObject.PreviewMouseLeftButtonDown += OnMouseDown;
        AssociatedObject.PreviewMouseMove += OnMouseMove;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.PreviewMouseLeftButtonDown -= OnMouseDown;
        AssociatedObject.PreviewMouseMove -= OnMouseMove;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e) =>
        _startPoint = e.GetPosition(null);

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        var diff = _startPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (AssociatedObject.SelectedItems.Count == 0)
            return;

        var items = AssociatedObject.SelectedItems.Cast<FileItemViewModel>().ToList();
        var data = new DataObject(typeof(List<FileItemViewModel>), items);
        DragDrop.DoDragDrop(AssociatedObject, data, DragDropEffects.Copy);
    }
}

public sealed class DropCommandBehavior : Behavior<FrameworkElement>
{
    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(
            nameof(Command),
            typeof(ICommand),
            typeof(DropCommandBehavior));

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    protected override void OnAttached()
    {
        AssociatedObject.AllowDrop = true;
        AssociatedObject.DragOver += OnDragOver;
        AssociatedObject.Drop += OnDrop;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.DragOver -= OnDragOver;
        AssociatedObject.Drop -= OnDrop;
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        var hasPayload = e.Data.GetDataPresent(DataFormats.FileDrop)
                         || e.Data.GetDataPresent(typeof(List<FileItemViewModel>));
        e.Effects = hasPayload ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (Command?.CanExecute(e) == true)
            Command.Execute(e);
        e.Handled = true;
    }
}
