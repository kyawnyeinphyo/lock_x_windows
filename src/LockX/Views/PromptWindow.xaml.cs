using System.Windows;

namespace LockX.Views;

public partial class PromptWindow : Window
{
    public string Value => Input.Text;

    public PromptWindow(string title, string label, string value)
    {
        InitializeComponent();
        Title = title;
        Label.Text = label;
        Input.Text = value;
        App.Vault.Locking += CloseOnLock;
        Closed += (_, _) => App.Vault.Locking -= CloseOnLock;
        Loaded += (_, _) =>
        {
            Input.Focus();
            // Select the name without its extension.
            int dot = value.LastIndexOf('.');
            Input.Select(0, dot > 0 ? dot : value.Length);
        };
    }

    void CloseOnLock() => Dispatcher.Invoke(() => { DialogResult = false; });

    void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
