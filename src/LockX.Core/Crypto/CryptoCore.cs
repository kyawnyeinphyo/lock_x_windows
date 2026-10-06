using System.Security.Cryptography;
using System.Text;

namespace LockX.Core.Crypto;

public class CryptoException : Exception
{
    public CryptoException(string message) : base(message) { }
}

public sealed class WrongPasswordException : CryptoException
{
    public WrongPasswordException() : base("Incorrect password.") { }
}

public sealed class VaultLockedException : CryptoException
{
    public VaultLockedException() : base("The vault is locked.") { }
}

/// <summary>Key derivation helpers.</summary>
public static class Kdf
{
    /// <summary>Default PBKDF2 work factor (same as the macOS build).</summary>
    public const int DefaultRounds = 310_000;

    public static byte[] RandomBytes(int count) => RandomNumberGenerator.GetBytes(count);

    /// <summary>Derives a 256-bit key-encryption key from a user password.</summary>
    public static byte[] PasswordKey(string password, byte[] salt, int rounds) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, rounds, HashAlgorithmName.SHA256, 32);

    /// <summary>Derives a purpose-specific subkey from the vault master key.</summary>
    public static byte[] Subkey(byte[] master, string label, byte[]? salt = null) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt ?? Array.Empty<byte>(), Encoding.UTF8.GetBytes(label));
}

/// <summary>Seal / open a whole blob in one shot. Layout: nonce(12) | ciphertext | tag(16),
/// identical to CryptoKit's AES.GCM "combined" representation.</summary>
public static class Sealed
{
    public const int NonceSize = 12;
    public const int TagSize = 16;

    public static byte[] Seal(ReadOnlySpan<byte> plaintext, byte[] key, ReadOnlySpan<byte> aad = default)
    {
        var output = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(NonceSize, plaintext.Length),
                    output.AsSpan(NonceSize + plaintext.Length, TagSize), aad);
        return output;
    }

    public static byte[] Open(ReadOnlySpan<byte> combined, byte[] key, ReadOnlySpan<byte> aad = default)
    {
        if (combined.Length < NonceSize + TagSize) throw new CryptoException("The encrypted data is corrupt.");
        int len = combined.Length - NonceSize - TagSize;
        var plain = new byte[len];
        using var aes = new AesGcm(key, TagSize);
        try
        {
            aes.Decrypt(combined[..NonceSize], combined.Slice(NonceSize, len),
                        combined[(NonceSize + len)..], plain, aad);
        }
        catch (CryptographicException)
        {
            throw new CryptoException("The encrypted data is corrupt or the key is wrong.");
        }
        return plain;
    }
}
