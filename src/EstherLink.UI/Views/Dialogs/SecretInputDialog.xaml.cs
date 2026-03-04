using System.Windows;

namespace EstherLink.UI.Views.Dialogs;

public partial class SecretInputDialog : Window
{
    public string SecretValue { get; private set; } = string.Empty;

    public SecretInputDialog(string title, string description, string? initialValue = null)
    {
        InitializeComponent();
        Title = title;
        PromptTitleTextBlock.Text = title;
        PromptDescriptionTextBlock.Text = description;

        var value = initialValue ?? string.Empty;
        HiddenSecretPasswordBox.Password = value;
        VisibleSecretTextBox.Text = value;
        UpdateInputVisibility();
    }

    private void ShowSecretCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        UpdateInputVisibility();
    }

    private void HiddenSecretPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (ShowSecretCheckBox.IsChecked != true)
        {
            VisibleSecretTextBox.Text = HiddenSecretPasswordBox.Password;
        }
    }

    private void VisibleSecretTextBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ShowSecretCheckBox.IsChecked == true)
        {
            HiddenSecretPasswordBox.Password = VisibleSecretTextBox.Text;
        }
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        SecretValue = ShowSecretCheckBox.IsChecked == true
            ? VisibleSecretTextBox.Text
            : HiddenSecretPasswordBox.Password;

        DialogResult = true;
        Close();
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void UpdateInputVisibility()
    {
        var show = ShowSecretCheckBox.IsChecked == true;
        VisibleSecretTextBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        HiddenSecretPasswordBox.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show)
        {
            VisibleSecretTextBox.Focus();
            VisibleSecretTextBox.CaretIndex = VisibleSecretTextBox.Text.Length;
        }
        else
        {
            HiddenSecretPasswordBox.Focus();
        }
    }
}
