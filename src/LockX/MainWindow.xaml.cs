using System.Windows;
using LockX.Core.Data;
using LockX.Views;

namespace LockX;

/// <summary>Hosts whichever screen matches the vault state: setup, lock or the browser.</summary>
public partial class MainWindow : Window
{
    VaultState? _shown;

    public MainWindow()
    {
        InitializeComponent();
        App.Vault.StateChanged += () => Dispatcher.Invoke(ShowCurrent);
        PreviewMouseMove += (_, _) => App.Session?.NoteActivity();
        PreviewMouseDown += (_, _) => App.Session?.NoteActivity();
        PreviewKeyDown += (_, _) => App.Session?.NoteActivity();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) App.Session?.LockIfConfigured();
        };
        ShowCurrent();
    }

    void ShowCurrent()
    {
        var state = App.Vault.State;
        if (state == _shown) return;
        _shown = state;
        Host.Content = state switch
        {
            VaultState.Uninitialized => new SetupView(),
            VaultState.Locked => new LockView(),
            _ => new MainView(),
        };
    }
}
