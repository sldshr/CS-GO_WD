using Avalonia.Controls;

namespace CsgoWorkshop.App;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog() => InitializeComponent();

    public static Task<bool> AskAsync(Window owner, string title, string message, string yesText)
    {
        var dialog = new ConfirmDialog { Title = title };
        dialog.MessageText.Text = message;
        dialog.YesButton.Content = yesText;
        dialog.Opened += (_, _) =>
        {
            dialog.Activate();
            dialog.YesButton.Focus();
        };
        dialog.YesButton.Click += (_, _) => dialog.Close(true);
        dialog.NoButton.Click += (_, _) => dialog.Close(false);
        return dialog.ShowDialog<bool>(owner);
    }
}
