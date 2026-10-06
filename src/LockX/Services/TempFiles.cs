using System.Diagnostics;
using System.IO;
using LockX.Core.Data;

namespace LockX.Services;

/// <summary>
/// Temporary decrypted copies for "Open with default app". They exist only so another
/// program can read the file; they are wiped on lock, on exit and at the next start.
/// </summary>
public static class TempFiles
{
    static readonly string Dir = Path.Combine(Path.GetTempPath(), "LockX-open");
    static bool _dirty;

    public static async Task<string> ExportAndOpenAsync(Vault vault, VaultItem item)
    {
        Directory.CreateDirectory(Dir);
        var folder = Path.Combine(Dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var safeName = string.Concat(item.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = Path.Combine(folder, safeName);
        await vault.ExportAsync(item, path);
        _dirty = true;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return path;
    }

    public static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(Dir)) { _dirty = false; return; }
            foreach (var d in Directory.EnumerateDirectories(Dir))
            {
                try { Directory.Delete(d, true); } catch { /* still open in another program: retry later */ }
            }
            _dirty = Directory.EnumerateFileSystemEntries(Dir).Any();
        }
        catch { }
    }

    /// <summary>Retries leftovers (files that were still open when the vault locked).</summary>
    public static void CleanupIfIdle()
    {
        if (_dirty) Cleanup();
    }
}
