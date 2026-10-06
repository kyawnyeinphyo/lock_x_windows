using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LockX.Core.Data;

namespace LockX.Views;

public partial class LockView : UserControl
{
    bool _working;
    bool HelloOn => App.Vault.Meta?.BiometricsEnabled == true;

    public LockView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (HelloOn)
            {
                ShowHello();
                // Only pop the prompt if the user is actually looking at us.
                if (Window.GetWindow(this)?.IsActive == true) OnHello(this, new RoutedEventArgs());
            }
            else ShowPassword(allowHello: false);
        };
    }

    void ShowHello()
    {
        HelloPanel.Visibility = Visibility.Visible;
        PasswordPanel.Visibility = Visibility.Collapsed;
        Password.Clear();
    }

    void ShowPassword(bool allowHello)
    {
        HelloPanel.Visibility = Visibility.Collapsed;
        PasswordPanel.Visibility = Visibility.Visible;
        BackToHello.Visibility = allowHello ? Visibility.Visible : Visibility.Collapsed;
        Password.Focus();
    }

    void SetError(string? message)
    {
        Error.Text = message ?? "";
        Error.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnShowPassword(object sender, RoutedEventArgs e) { SetError(null); ShowPassword(HelloOn); }
    void OnShowHello(object sender, RoutedEventArgs e) { SetError(null); ShowHello(); }

    async void OnHello(object sender, RoutedEventArgs e)
    {
        if (_working) return;
        _working = true;
        HelloButton.IsEnabled = false;
        SetError(null);
        try
        {
            await App.Session.WhileAuthenticating(() => App.Vault.UnlockWithDeviceAsync());
        }
        catch (UnlockCancelledException) { /* stay on the lock screen */ }
        catch (Exception ex)
        {
            SetError(ex.Message);
            ShowPassword(allowHello: true);
        }
        _working = false;
        HelloButton.IsEnabled = true;
    }

    void OnPasswordChanged(object sender, RoutedEventArgs e) =>
        UnlockButton.IsEnabled = !_working && Password.Password.Length > 0;

    void OnPasswordKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && UnlockButton.IsEnabled) OnUnlock(sender, e);
    }

    async void OnUnlock(object sender, RoutedEventArgs e)
    {
        if (_working || Password.Password.Length == 0) return;
        _working = true;
        UnlockButton.IsEnabled = false;
        SetError(null);
        try
        {
            await App.Vault.UnlockWithPasswordAsync(Password.Password);
            Password.Clear();
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
            Password.SelectAll();
        }
        _working = false;
        OnPasswordChanged(this, e);
    }
}
