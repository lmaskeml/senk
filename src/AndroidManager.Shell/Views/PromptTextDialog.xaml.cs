using System.Windows;

namespace AndroidManager.Shell.Views;

public partial class PromptTextDialog : Window
{
    public string? Value { get; private set; }

    public PromptTextDialog(string title, string message, string? defaultValue = null)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        InputBox.Text = defaultValue ?? string.Empty;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Value = InputBox.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
