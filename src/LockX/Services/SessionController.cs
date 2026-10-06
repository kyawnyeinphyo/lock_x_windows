using System.Windows;
using System.Windows.Threading;
using LockX.Core.Data;
using Microsoft.Win32;

namespace LockX.Services;

/// <summary>Owns the idle timer and every "lock now" trigger (focus loss, minimise, Windows lock, sleep).</summary>
public sealed class SessionController
{
    readonly Vault _vault;
    readonly DispatcherTimer _timer;
    DateTime _lastActivity = DateTime.UtcNow;
    int _suppress;

    public int? SecondsUntilLock { get; private set; }
    public event Action? Ticked;

    public SessionController(Vault vault)
    {
        _vault = vault;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        Application.Current.Deactivated += (_, _) => LockIfConfigured();
        SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect
                or SessionSwitchReason.RemoteDisconnect)
                Application.Current.Dispatcher.Invoke(_vault.Lock);
        };
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.Suspend) Application.Current.Dispatcher.Invoke(_vault.Lock);
        };
    }

    public void NoteActivity() => _lastActivity = DateTime.UtcNow;

    /// <summary>Lock on hide / focus loss, unless a system prompt (Windows Hello) is what took the focus.</summary>
    public void LockIfConfigured()
    {
        if (_suppress > 0) return;
        if (_vault.State == VaultState.Unlocked && _vault.Meta?.LockOnHide == true)
            _vault.Lock();
    }

    /// <summary>Runs <paramref name="work"/> without focus loss locking the vault; for the Windows Hello prompt.</summary>
    public async Task<T> WhileAuthenticating<T>(Func<Task<T>> work)
    {
        _suppress++;
        try { return await work(); }
        finally
        {
            // The prompt's window closes a moment after the call returns.
            await Task.Delay(500);
            _suppress--;
            NoteActivity();
        }
    }

    public Task WhileAuthenticating(Func<Task> work) =>
        WhileAuthenticating(async () => { await work(); return 0; });

    void Tick()
    {
        TempFiles.CleanupIfIdle();
        if (_vault.State != VaultState.Unlocked || _vault.Meta is not { AutoLockSeconds: > 0 } meta)
        {
            SecondsUntilLock = null;
            Ticked?.Invoke();
            return;
        }
        int idle = (int)(DateTime.UtcNow - _lastActivity).TotalSeconds;
        SecondsUntilLock = Math.Max(0, meta.AutoLockSeconds - idle);
        Ticked?.Invoke();
        if (idle >= meta.AutoLockSeconds) _vault.Lock();
    }
}
