using AgentBridge.App.Security;
using System.Windows;

namespace AgentBridge.App;

/// <summary>
/// The startup login gate. Shown before anything else in the process — no
/// DI container, no services — so a wrong password costs nothing beyond
/// reading the settings file for the theme.
/// </summary>
public partial class LoginWindow : System.Windows.Window
{
    public LoginWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PasswordInput.Focus();
    }

    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (LoginPasswordHasher.Verify(PasswordInput.Password))
        {
            DialogResult = true;
            return;
        }

        ErrorText.Visibility = Visibility.Visible;
        PasswordInput.Clear();
        PasswordInput.Focus();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
