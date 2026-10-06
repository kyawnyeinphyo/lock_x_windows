using System.Security.Cryptography;
using System.Text;
using LockX.Core.Crypto;
using LockX.Core.Data;
using Xunit;

namespace LockX.Core.Tests;

public class BlobTests
{
    static byte[] Key() => RandomNumberGenerator.GetBytes(32);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(4096)]
    [InlineData(4097)]
    public void RoundTrip(int size)
    {
        var key = Key(); var id = Guid.NewGuid();
        var data = RandomNumberGenerator.GetBytes(size);
        var blob = BlobCrypto.Encrypt(data, key, id, chunkSize: 1024);
        Assert.Equal(data, BlobCrypto.Decrypt(blob, key, id));
    }

    [Fact]
    public void RandomAccessReaderMatchesPlaintext()
    {
        var key = Key(); var id = Guid.NewGuid();
        var data = RandomNumberGenerator.GetBytes(10_000);
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, BlobCrypto.Encrypt(data, key, id, chunkSize: 1024));
            using var r = new BlobReader(path, key, id);
            Assert.Equal(data.Length, r.PlainSize);
            Assert.Equal(data[1000..3500], r.Read(1000, 2500));
            Assert.Equal(data[9990..], r.Read(9990, 500));
            Assert.Empty(r.Read(10_000, 10));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ChunksAreBoundToItemAndIndex()
    {
        var key = Key();
        var data = RandomNumberGenerator.GetBytes(3000);
        var blob = BlobCrypto.Encrypt(data, key, Guid.NewGuid(), chunkSize: 1024);
        Assert.Throws<CryptoException>(() => BlobCrypto.Decrypt(blob, key, Guid.NewGuid()));

        // Swap chunk 0 and 1.
        var step = 1024 + BlobCrypto.ChunkOverhead;
        var swapped = (byte[])blob.Clone();
        Array.Copy(blob, BlobCrypto.HeaderSize, swapped, BlobCrypto.HeaderSize + step, step);
        Array.Copy(blob, BlobCrypto.HeaderSize + step, swapped, BlobCrypto.HeaderSize, step);
        Assert.Throws<CryptoException>(() => BlobCrypto.Decrypt(swapped, key, Guid.Empty));
    }

    [Fact]
    public void CiphertextMatchesMacFormat()
    {
        // Header layout is shared with the macOS build: "LOCKXBLB", v1, 3 reserved, chunk LE, size LE.
        var blob = BlobCrypto.Encrypt(new byte[5], Key(), Guid.NewGuid(), chunkSize: 1024);
        Assert.Equal("LOCKXBLB", Encoding.ASCII.GetString(blob, 0, 8));
        Assert.Equal(1, blob[8]);
        Assert.Equal(1024u, BitConverter.ToUInt32(blob, 12));
        Assert.Equal(5, BitConverter.ToInt64(blob, 16));
        Assert.Equal(BlobCrypto.HeaderSize + 5 + BlobCrypto.ChunkOverhead, blob.Length);
    }
}

public class VaultTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "lockx-test-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task CreateLockUnlockRoundTrip()
    {
        var v = new Vault(_root);
        Assert.Equal(VaultState.Uninitialized, v.State);
        await v.CreateVaultAsync("correct horse", false);
        v.CreateNote("hello", "secret body");
        var src = Path.Combine(_root, "plain.txt");
        File.WriteAllText(src, "file contents");
        await v.ImportFileAsync(src);
        v.Lock();
        Assert.Equal(VaultState.Locked, v.State);
        Assert.Empty(v.Items);

        // New instance = app restart.
        var v2 = new Vault(_root);
        Assert.Equal(VaultState.Locked, v2.State);
        await Assert.ThrowsAsync<WrongPasswordException>(() => v2.UnlockWithPasswordAsync("nope"));
        await v2.UnlockWithPasswordAsync("correct horse");
        Assert.Equal(2, v2.Items.Count);
        var note = v2.ItemsOf(ItemKind.Note).Single();
        Assert.Equal("secret body", v2.NoteText(note));
        var file = v2.ItemsOf(ItemKind.File).Single();
        Assert.Equal("file contents", Encoding.UTF8.GetString(v2.GetData(file)));
    }

    [Fact]
    public async Task ChangePasswordKeepsData()
    {
        var v = new Vault(_root);
        await v.CreateVaultAsync("old password", false);
        v.CreateNote("n", "body");
        await Assert.ThrowsAsync<WrongPasswordException>(() => v.ChangePasswordAsync("bad", "new password"));
        await v.ChangePasswordAsync("old password", "new password");
        v.Lock();
        await Assert.ThrowsAsync<WrongPasswordException>(() => v.UnlockWithPasswordAsync("old password"));
        await v.UnlockWithPasswordAsync("new password");
        Assert.Equal("body", v.NoteText(v.Items.Single()));
    }

    [Fact]
    public async Task DamagedMetaIsNeverTreatedAsNoVault()
    {
        var v = new Vault(_root);
        await v.CreateVaultAsync("pw pw pw pw", false);
        v.CreateNote("keep me", "important");
        v.Lock();

        File.WriteAllText(Path.Combine(_root, "vault.json"), "{ not json");
        Assert.Throws<CryptoException>(() => new Vault(_root));
        // And the damaged file is left exactly as it was.
        Assert.Equal("{ not json", File.ReadAllText(Path.Combine(_root, "vault.json")));
    }

    [Fact]
    public async Task CannotCreateOverExistingVault()
    {
        var v = new Vault(_root);
        await v.CreateVaultAsync("pw pw pw pw", false);
        await Assert.ThrowsAsync<CryptoException>(() => new Vault(_root).CreateVaultAsync("other other", false));
    }

    [Fact]
    public async Task NothingOnDiskIsPlaintext()
    {
        var v = new Vault(_root);
        await v.CreateVaultAsync("pw pw pw pw", false);
        v.CreateNote("title-marker", "body-marker");
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(f));
            Assert.DoesNotContain("body-marker", text);
            Assert.DoesNotContain("title-marker", text);
        }
    }
}
