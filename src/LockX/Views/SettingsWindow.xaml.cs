using System.Windows;
using System.Windows.Controls;
using LockX.Core.Data;

namespace LockX.Views;

public partial class SettingsWindow : Window
{
    static readonly KeyValuePair<string, int>[] Choices =
    {
        new("1 minute", 60), new("5 minutes", 300), new("15 minutes", 900), new("1 hour", 3600), new("Never", 0),
    };

    readonly Vault _vault = App.Vault;
    bool _loading = true;

    public SettingsWindow()
    {
        InitializeComponent();
        AutoLock.ItemsSource = Choices;
        var meta = _vault.Meta!;
        AutoLock.SelectedValue = Choices.Any(c => c.Value == meta.AutoLockSeconds) ? meta.AutoLockSeconds : 300;
        LockOnHide.IsChecked = meta.LockOnHide;
        Hello.IsChecked = meta.BiometricsEnabled;
        PathText.Text = _vault.Root;
        _loading = false;

        _vault.Locking += CloseOnLock;
        Closed += (_, _) => _vault.Locking -= CloseOnLock;
        Loaded += async (_, _) =>
        {
            if (await _vault.DeviceUnlockAvailableAsync()) Hello.Visibility = Visibility.Visible;
        };
    }

    void CloseOnLock() => Dispatcher.Invoke(Close);

    void OnAutoLockChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && AutoLock.SelectedValue is int s) _vault.UpdateSettings(autoLockSeconds: s);
    }

    void OnLockOnHideChanged(object sender, RoutedEventArgs e) =>
        _vault.UpdateSettings(lockOnHide: LockOnHide.IsChecked == true);

    async void OnHelloClicked(object sender, RoutedEventArgs e)
    {
        bool want = Hello.IsChecked == true;
        Error.Visibility = Visibility.Collapsed;
        Hello.IsEnabled = false;
        try
        {
            await App.Session.WhileAuthenticating(() => _vault.SetBiometricsAsync(want));
        }
        catch (Exception ex)
        {
            Hello.IsChecked = !want;
            if (ex is not UnlockCancelledException)
            {
                Error.Text = ex.Message;
                Error.Visibility = Visibility.Visible;
            }
        }
        Hello.IsEnabled = true;
        Activate();
    }

    void OnPwChanged(object sender, RoutedEventArgs e)
    {
        Strength.SetPassword(New.Password);
        ChangeButton.IsEnabled = Current.Password.Length > 0 && New.Password.Length >= 8 && New.Password == Confirm.Password;
    }

    async void OnChangePassword(object sender, RoutedEventArgs e)
    {
        ChangeButton.IsEnabled = false;
        try
        {
            await _vault.ChangePasswordAsync(Current.Password, New.Password);
            ShowMessage("Password changed.", error: false);
            Current.Clear(); New.Clear(); Confirm.Clear();
        }
        catch (Exception ex) { ShowMessage(ex.Message, error: true); }
        OnPwChanged(this, e);
    }

    void ShowMessage(string text, bool error)
    {
        Message.Text = text;
        Message.SetResourceReference(TextBlock.ForegroundProperty,
                                     error ? "SystemFillColorCriticalBrush" : "SystemFillColorSuccessBrush");
        Message.Visibility = Visibility.Visible;
    }
}
