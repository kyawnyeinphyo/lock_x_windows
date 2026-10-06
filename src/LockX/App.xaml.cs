using System.IO;
using System.Windows;
using LockX.Core.Data;
using LockX.Services;

namespace LockX;

public partial class App : Application
{
    Mutex? _singleInstance;

    public static Vault Vault { get; private set; } = null!;
    public static SessionController Session { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (Environment.GetEnvironmentVariable("LOCKX_DUMP_KEYS") == "1")
        {
            DumpResourceKeys();
            Shutdown();
            return;
        }

        // Two instances would race on the same encrypted index.
        _singleInstance = new Mutex(true, @"Local\LockX.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("Lock X is already running.", "Lock X", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        TempFiles.Cleanup();   // leftovers from a crash

        try
        {
            var root = Environment.GetEnvironmentVariable("LOCKX_ROOT");   // handy for testing
            Vault = new Vault(string.IsNullOrWhiteSpace(root) ? null : root,
                              new WindowsHelloUnlock(), new MediaInspector());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Lock X could not open its vault folder.\n\n{ex.Message}", "Lock X",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        Session = new SessionController(Vault);
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Vault?.Lock(); } catch { }
        TempFiles.Cleanup();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    void DumpResourceKeys()
    {
        var keys = new List<string>();
        void Walk(ResourceDictionary d)
        {
            foreach (var k in d.Keys) keys.Add(k.ToString() ?? "");
            foreach (var m in d.MergedDictionaries) Walk(m);
        }
        Walk(Resources);
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "lockx-keys.txt"), keys.Distinct().OrderBy(k => k));
    }
}
