using System.Windows;
using System.Windows.Controls;
using LockX.Core.Data;

namespace LockX.Views;

/// <summary>First-run screen: choose the recovery password and opt into Windows Hello.</summary>
public partial class SetupView : UserControl
{
    bool _working;

    public SetupView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            Password.Focus();
            if (await App.Vault.DeviceUnlockAvailableAsync())
            {
                Hello.Visibility = Visibility.Visible;
                Hello.IsChecked = true;
            }
        };
    }

    void OnChanged(object sender, RoutedEventArgs e)
    {
        var pw = Password.Password;
        Strength.SetPassword(pw);
        if (pw.Length > 0 && pw.Length < 8)
        {
            Hint.Text = "Use at least 8 characters.";
            Hint.Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush");
        }
        else if (Confirm.Password.Length > 0 && pw != Confirm.Password)
        {
            Hint.Text = "Passwords do not match.";
            Hint.Foreground = (System.Windows.Media.Brush)FindResource("SystemFillColorCriticalBrush");
        }
        else Hint.Text = "";
        Create.IsEnabled = !_working && pw.Length >= 8 && pw == Confirm.Password;
    }

    async void OnCreate(object sender, RoutedEventArgs e)
    {
        _working = true;
        Create.IsEnabled = false;
        Error.Visibility = Visibility.Collapsed;
        bool hello = Hello.IsChecked == true;
        try
        {
            if (hello)
                await App.Session.WhileAuthenticating(() => App.Vault.CreateVaultAsync(Password.Password, true));
            else
                await App.Vault.CreateVaultAsync(Password.Password, false);
        }
        catch (UnlockCancelledException)
        {
            Error.Text = "Windows Hello setup was cancelled. Untick the option to continue without it.";
            Error.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Error.Text = ex.Message;
            Error.Visibility = Visibility.Visible;
        }
        _working = false;
        // On success this view is replaced; on failure let the user retry.
        OnChanged(this, e);
    }
}
