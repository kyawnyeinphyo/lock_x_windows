using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LockX.Core.Crypto;

namespace LockX.Core.Data;

public enum VaultState { Uninitialized, Locked, Unlocked }

/// <summary>
/// The vault: key management, the encrypted item index, and all item I/O.
///
/// Everything on disk is ciphertext. The only plaintext file is vault.json,
/// which holds the salt and the password-wrapped master key.
/// All members must be called from one thread (the UI thread); heavy crypto
/// is pushed to the thread pool internally.
/// </summary>
public sealed class Vault
{
    static readonly byte[] WrapAad = Encoding.UTF8.GetBytes("lockx.wrap");
    static readonly byte[] IndexAad = Encoding.UTF8.GetBytes("lockx.index");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    readonly IDeviceUnlock? _device;
    readonly IMediaInspector? _inspector;
    byte[]? _masterKey;
    byte[]? _indexKey;
    List<VaultItem> _items = new();

    public VaultState State { get; private set; } = VaultState.Locked;
    public VaultMeta? Meta { get; private set; }
    public IReadOnlyList<VaultItem> Items => _items;
    public string Root { get; }

    public event Action? StateChanged;
    public event Action? ItemsChanged;
    /// <summary>Raised just before key material is dropped, so viewers can release streams and temp files.</summary>
    public event Action? Locking;

    string MetaPath => Path.Combine(Root, "vault.json");
    string IndexPath => Path.Combine(Root, "index.enc");
    string BlobsDir => Path.Combine(Root, "blobs");
    string ThumbsDir => Path.Combine(Root, "thumbs");

    public string BlobPath(Guid id) => Path.Combine(BlobsDir, id.ToString() + ".lxb");
    string ThumbPath(Guid id) => Path.Combine(ThumbsDir, id.ToString() + ".lxt");

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LockX");

    public Vault(string? root = null, IDeviceUnlock? device = null, IMediaInspector? inspector = null)
    {
        Root = root ?? DefaultRoot;
        _device = device;
        _inspector = inspector;
        Directory.CreateDirectory(BlobsDir);
        Directory.CreateDirectory(ThumbsDir);
        LoadMeta();
    }

    public Task<bool> DeviceUnlockAvailableAsync() => _device?.IsAvailableAsync() ?? Task.FromResult(false);

    void LoadMeta()
    {
        Meta = null;
        State = VaultState.Uninitialized;
        if (!File.Exists(MetaPath)) return;

        // A vault.json that exists but cannot be read must never be mistaken for "no vault":
        // the setup screen would then overwrite it and orphan every encrypted item.
        VaultMeta? meta;
        try { meta = JsonSerializer.Deserialize<VaultMeta>(File.ReadAllText(MetaPath), Json); }   // ReadAllText drops a BOM
        catch (Exception ex)
        {
            throw new CryptoException($"The vault file \"{MetaPath}\" is damaged or unreadable ({ex.Message}). " +
                                      "Lock X will not touch it. Restore a backup, or move the folder away to start a new vault.");
        }
        if (meta is not { PasswordWrappedKey.Length: > 0 })
            throw new CryptoException($"The vault file \"{MetaPath}\" is incomplete. Lock X will not touch it.");
        Meta = meta;
        State = VaultState.Locked;
    }

    void SaveMeta()
    {
        if (Meta == null) return;
        var tmp = MetaPath + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(Meta, Json));
        File.Move(tmp, MetaPath, overwrite: true);
    }

    void SetState(VaultState s)
    {
        State = s;
        StateChanged?.Invoke();
    }

    // ---- Setup ------------------------------------------------------------

    /// <summary>Creates a brand-new vault protected by <paramref name="password"/>.</summary>
    public async Task CreateVaultAsync(string password, bool enableBiometrics)
    {
        if (Meta != null || File.Exists(MetaPath))
            throw new CryptoException("A vault already exists in this folder.");
        var master = RandomNumberGenerator.GetBytes(32);
        var salt = Kdf.RandomBytes(32);
        int rounds = Kdf.DefaultRounds;
        // PBKDF2 is deliberately slow: never run it on the UI thread.
        var wrapped = await Task.Run(() =>
        {
            var kek = Kdf.PasswordKey(password, salt, rounds);
            try { return Sealed.Seal(master, kek, WrapAad); }
            finally { CryptographicOperations.ZeroMemory(kek); }
        });

        var meta = new VaultMeta { KdfSalt = salt, KdfRounds = rounds, PasswordWrappedKey = wrapped };
        if (enableBiometrics && _device != null)
            meta.DeviceWrap = await _device.EnrollAsync(meta.VaultId, master);

        Meta = meta;
        SaveMeta();
        Adopt(master);
        _items = new List<VaultItem>();
        WriteIndex();
        SetState(VaultState.Unlocked);
        ItemsChanged?.Invoke();
    }

    // ---- Lock / unlock ----------------------------------------------------

    public async Task UnlockWithPasswordAsync(string password)
    {
        var meta = Meta ?? throw new CryptoException("There is no vault yet.");
        var salt = meta.KdfSalt; int rounds = meta.KdfRounds; var wrapped = meta.PasswordWrappedKey;
        var master = await Task.Run(() =>
        {
            var kek = Kdf.PasswordKey(password, salt, rounds);
            try { return Sealed.Open(wrapped, kek, WrapAad); }
            catch (CryptoException) { throw new WrongPasswordException(); }
            finally { CryptographicOperations.ZeroMemory(kek); }
        });
        Adopt(master);
        FinishUnlock();
    }

    public async Task UnlockWithDeviceAsync()
    {
        var wrap = Meta?.DeviceWrap;
        if (wrap == null || _device == null) throw new VaultLockedException();
        var master = await _device.UnwrapAsync(wrap);
        Adopt(master);
        FinishUnlock();
    }

    void FinishUnlock()
    {
        try
        {
            LoadIndex();
        }
        catch (CryptoException)
        {
            // The key we recovered does not belong to this vault (e.g. a stale Hello key).
            Lock();
            throw new WrongPasswordException();
        }
        SetState(VaultState.Unlocked);
        ItemsChanged?.Invoke();
    }

    public void Lock()
    {
        if (_masterKey == null && State != VaultState.Unlocked) return;
        Locking?.Invoke();
        if (_masterKey != null) CryptographicOperations.ZeroMemory(_masterKey);
        if (_indexKey != null) CryptographicOperations.ZeroMemory(_indexKey);
        _masterKey = null;
        _indexKey = null;
        _items = new List<VaultItem>();
        ItemsChanged?.Invoke();
        if (Meta != null) SetState(VaultState.Locked);
    }

    void Adopt(byte[] master)
    {
        _masterKey = master;
        _indexKey = Kdf.Subkey(master, "lockx.index");
    }

    byte[] RequireKey() => _masterKey ?? throw new VaultLockedException();

    byte[] ItemKey(Guid id, string purpose = "lockx.item")
    {
        var salt = new byte[16];
        id.TryWriteBytes(salt, bigEndian: true, out _);
        return Kdf.Subkey(RequireKey(), purpose, salt);
    }

    // ---- Password / settings ---------------------------------------------

    public async Task ChangePasswordAsync(string current, string @new)
    {
        var meta = Meta ?? throw new CryptoException("There is no vault yet.");
        var oldSalt = meta.KdfSalt; int oldRounds = meta.KdfRounds; var oldWrapped = meta.PasswordWrappedKey;
        var salt = Kdf.RandomBytes(32);
        int rounds = Kdf.DefaultRounds;
        // Re-wrap the same master key with a fresh salt; item data is untouched.
        var rewrapped = await Task.Run(() =>
        {
            var oldKek = Kdf.PasswordKey(current, oldSalt, oldRounds);
            byte[] raw;
            try { raw = Sealed.Open(oldWrapped, oldKek, WrapAad); }
            catch (CryptoException) { throw new WrongPasswordException(); }
            finally { CryptographicOperations.ZeroMemory(oldKek); }
            var newKek = Kdf.PasswordKey(@new, salt, rounds);
            try { return Sealed.Seal(raw, newKek, WrapAad); }
            finally { CryptographicOperations.ZeroMemory(newKek); CryptographicOperations.ZeroMemory(raw); }
        });
        meta.KdfSalt = salt;
        meta.KdfRounds = rounds;
        meta.PasswordWrappedKey = rewrapped;
        SaveMeta();
    }

    public async Task SetBiometricsAsync(bool enabled)
    {
        var meta = Meta;
        if (meta == null || _device == null) return;
        if (enabled)
        {
            // Pass a copy: a lock while the Hello prompt is up zeroes our key in place.
            meta.DeviceWrap = await _device.EnrollAsync(meta.VaultId, (byte[])RequireKey().Clone());
        }
        else if (meta.DeviceWrap != null)
        {
            var old = meta.DeviceWrap;
            meta.DeviceWrap = null;
            SaveMeta();
            try { await _device.RemoveAsync(old); } catch { /* an orphaned key is harmless */ }
            return;
        }
        SaveMeta();
    }

    public void UpdateSettings(int? autoLockSeconds = null, bool? lockOnHide = null)
    {
        if (Meta == null) return;
        if (autoLockSeconds is { } a) Meta.AutoLockSeconds = a;
        if (lockOnHide is { } h) Meta.LockOnHide = h;
        try { SaveMeta(); } catch (IOException) { }
    }

    // ---- Index ------------------------------------------------------------

    void LoadIndex()
    {
        var key = _indexKey ?? throw new VaultLockedException();
        if (!File.Exists(IndexPath)) { _items = new List<VaultItem>(); return; }
        var plain = Sealed.Open(File.ReadAllBytes(IndexPath), key, IndexAad);
        _items = (JsonSerializer.Deserialize<List<VaultItem>>(plain, Json) ?? new())
            .OrderByDescending(i => i.AddedAt).ToList();
    }

    void WriteIndex()
    {
        var key = _indexKey ?? throw new VaultLockedException();
        var sealedIndex = Sealed.Seal(JsonSerializer.SerializeToUtf8Bytes(_items, Json), key, IndexAad);
        var tmp = IndexPath + ".tmp";
        File.WriteAllBytes(tmp, sealedIndex);
        File.Move(tmp, IndexPath, overwrite: true);
    }

    public IEnumerable<VaultItem> ItemsOf(ItemKind kind) => _items.Where(i => i.Kind == kind);
    public int CountOf(ItemKind kind) => _items.Count(i => i.Kind == kind);
    public VaultItem? Find(Guid id) => _items.FirstOrDefault(i => i.Id == id);

    // ---- Import -----------------------------------------------------------

    public async Task<VaultItem> ImportFileAsync(string source)
    {
        RequireKey();
        var kind = ItemKindInfo.Classify(source);
        var item = new VaultItem { Name = Path.GetFileName(source), Kind = kind };

        var id = item.Id;
        var blobKey = ItemKey(id);
        var thumbKey = ItemKey(id, "lockx.thumb");
        var blobDest = BlobPath(id);
        var thumbDest = ThumbPath(id);
        var inspector = _inspector;

        // Encrypting a large video would otherwise freeze the UI for seconds.
        var result = await Task.Run(async () =>
        {
            long size = BlobCrypto.EncryptFile(source, blobDest, blobKey, id);
            MediaInfo? info = null;
            bool madeThumb = false;
            if (inspector != null && kind != ItemKind.File)
            {
                try { info = await inspector.InspectAsync(source, kind).ConfigureAwait(false); }
                catch { /* metadata is best-effort */ }
                if (info?.ThumbnailPng is { Length: > 0 } png)
                {
                    BlobCrypto.Write(png, thumbDest, thumbKey, id);
                    madeThumb = true;
                }
            }
            return (size, info, madeThumb);
        });

        item.ByteSize = result.size;
        item.Duration = result.info?.Duration;
        item.PixelWidth = result.info?.Width;
        item.PixelHeight = result.info?.Height;
        item.HasThumbnail = result.madeThumb;

        _items.Insert(0, item);
        WriteIndex();
        ItemsChanged?.Invoke();
        return item;
    }

    // ---- Notes ------------------------------------------------------------

    public VaultItem CreateNote(string title, string body)
    {
        RequireKey();
        var bytes = Encoding.UTF8.GetBytes(body);
        var item = new VaultItem
        {
            Name = string.IsNullOrWhiteSpace(title) ? "Untitled Note" : title.Trim(),
            Kind = ItemKind.Note,
            ByteSize = bytes.Length,
        };
        BlobCrypto.Write(bytes, BlobPath(item.Id), ItemKey(item.Id), item.Id);
        _items.Insert(0, item);
        WriteIndex();
        ItemsChanged?.Invoke();
        return item;
    }

    public void UpdateNote(VaultItem item, string title, string body)
    {
        var existing = Find(item.Id);
        if (existing == null) return;
        var bytes = Encoding.UTF8.GetBytes(body);
        BlobCrypto.Write(bytes, BlobPath(item.Id), ItemKey(item.Id), item.Id);
        existing.Name = string.IsNullOrWhiteSpace(title) ? "Untitled Note" : title.Trim();
        existing.ByteSize = bytes.Length;
        existing.ModifiedAt = DateTime.UtcNow;
        WriteIndex();
        ItemsChanged?.Invoke();
    }

    public string NoteText(VaultItem item) => Encoding.UTF8.GetString(GetData(item));

    // ---- Reading ----------------------------------------------------------

    public byte[] GetData(VaultItem item) =>
        BlobCrypto.ReadAll(BlobPath(item.Id), ItemKey(item.Id), item.Id);

    /// <summary>Decrypts off the UI thread (large images).</summary>
    public Task<byte[]> GetDataAsync(VaultItem item)
    {
        var path = BlobPath(item.Id); var key = ItemKey(item.Id); var id = item.Id;
        return Task.Run(() => BlobCrypto.ReadAll(path, key, id));
    }

    /// <summary>Random-access reader, used to stream video without decrypting the whole file.</summary>
    public BlobReader OpenReader(VaultItem item) =>
        new(BlobPath(item.Id), ItemKey(item.Id), item.Id);

    public byte[]? GetThumbnailPng(VaultItem item)
    {
        if (!item.HasThumbnail || _masterKey == null) return null;
        try { return BlobCrypto.ReadAll(ThumbPath(item.Id), ItemKey(item.Id, "lockx.thumb"), item.Id); }
        catch { return null; }
    }

    // ---- Export / delete / rename ----------------------------------------

    public Task ExportAsync(VaultItem item, string destination)
    {
        var path = BlobPath(item.Id); var key = ItemKey(item.Id); var id = item.Id;
        return Task.Run(() => BlobCrypto.ExportFile(path, destination, key, id));
    }

    public void Delete(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        foreach (var id in set)
        {
            try { File.Delete(BlobPath(id)); } catch { }
            try { File.Delete(ThumbPath(id)); } catch { }
        }
        _items.RemoveAll(i => set.Contains(i.Id));
        WriteIndex();
        ItemsChanged?.Invoke();
    }

    public void Rename(VaultItem item, string newName)
    {
        var existing = Find(item.Id);
        if (existing == null || string.IsNullOrWhiteSpace(newName)) return;
        existing.Name = newName.Trim();
        existing.ModifiedAt = DateTime.UtcNow;
        WriteIndex();
        ItemsChanged?.Invoke();
    }
}
