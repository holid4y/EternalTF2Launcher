using System.Windows;
using System.Windows.Input;

namespace LauncherTF2.Views;

public partial class InputDialog : Window
{
    public string InputText => InputTextBox.Text;
    public string DescriptionText => DescriptionTextBox.Text;

    public InputDialog(string title, string prompt, string defaultName = "", bool showDescription = false)
    {
        InitializeComponent();
        TitleText.Text    = title;
        PromptText.Text   = prompt;
        InputTextBox.Text = defaultName;
        InputTextBox.SelectAll();
        InputTextBox.Focus();

        if (!showDescription)
        {
            DescriptionLabel.Visibility      = Visibility.Collapsed;
            DescriptionTextBox.Visibility    = Visibility.Collapsed;
        }
    }

    // Allow dragging the windowless dialog.
    private void Grid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputTextBox.Text)) return;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OkButton_Click(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.Escape)
        {
            CancelButton_Click(this, new RoutedEventArgs());
        }
    }
}
