using Avalonia.Controls;
using XJournal.App.Controls;
using XJournal.App.ViewModels;

namespace XJournal.App.Views;

public partial class LoginWindow : JournalWindow
{
    public LoginWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (DataContext is LoginViewModel { Username.Length: > 0 }) PasswordBox.Focus();
            else UsernameBox.Focus();
        };
    }
}
