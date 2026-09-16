using System.Windows;
using Microsoft.Xaml.Behaviors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace AndroidManager.Shell.Behaviors;

public sealed class DigitBoxBehavior : Behavior<TextBox>
{
    public static readonly DependencyProperty NextBoxProperty =
        DependencyProperty.Register(nameof(NextBox), typeof(TextBox), typeof(DigitBoxBehavior));

    public static readonly DependencyProperty PrevBoxProperty =
        DependencyProperty.Register(nameof(PrevBox), typeof(TextBox), typeof(DigitBoxBehavior));

    public TextBox? NextBox
    {
        get => (TextBox?)GetValue(NextBoxProperty);
        set => SetValue(NextBoxProperty, value);
    }

    public TextBox? PrevBox
    {
        get => (TextBox?)GetValue(PrevBoxProperty);
        set => SetValue(PrevBoxProperty, value);
    }

    protected override void OnAttached()
    {
        AssociatedObject.PreviewTextInput += OnTextInput;
        AssociatedObject.PreviewKeyDown += OnPreviewKeyDown;
        AssociatedObject.GotFocus += OnGotFocus;
        System.Windows.DataObject.AddPastingHandler(AssociatedObject, OnPaste);
    }

    protected override void OnDetaching()
    {
        AssociatedObject.PreviewTextInput -= OnTextInput;
        AssociatedObject.PreviewKeyDown -= OnPreviewKeyDown;
        AssociatedObject.GotFocus -= OnGotFocus;
        System.Windows.DataObject.RemovePastingHandler(AssociatedObject, OnPaste);
    }

    private void OnTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        if (e.Text.Length == 0 || !e.Text.All(char.IsDigit))
        {
            e.Handled = true;
            return;
        }

        AssociatedObject.Text = e.Text[^1].ToString();
        e.Handled = true;
        NextBox?.Focus();
        NextBox?.SelectAll();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Back && string.IsNullOrEmpty(AssociatedObject.Text))
        {
            PrevBox?.Focus();
            PrevBox?.SelectAll();
            e.Handled = true;
        }
    }

    private void OnGotFocus(object sender, RoutedEventArgs e) => AssociatedObject.SelectAll();

    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(System.Windows.DataFormats.Text))
        {
            e.CancelCommand();
            return;
        }

        var text = e.DataObject.GetData(System.Windows.DataFormats.Text) as string ?? string.Empty;
        var digit = text.FirstOrDefault(char.IsDigit);
        if (digit == '\0')
        {
            e.CancelCommand();
            return;
        }

        e.CancelCommand();
        AssociatedObject.Text = digit.ToString();
        NextBox?.Focus();
        NextBox?.SelectAll();
    }
}
