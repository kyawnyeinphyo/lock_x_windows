using System.Security.Cryptography;
using System.Text;
using LockX.Core.Crypto;
using LockX.Core.Data;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;

namespace LockX.Services;

/// <summary>
/// Windows Hello unlock, the counterpart of the Secure Enclave on macOS.
///
/// Hello creates an RSA key pair held by the TPM (or in software if there is no TPM)
/// that can only sign after the user passes face / fingerprint / PIN. RSA PKCS#1 v1.5
/// signatures are deterministic, so signing a stored random challenge always yields the
/// same bytes. Those bytes are run through HKDF to give a key-encryption key that wraps
/// the vault master key. The wrap is useless on any other PC or Windows account.
/// </summary>
public sealed class WindowsHelloUnlock : IDeviceUnlock
{
    static readonly byte[] HkdfSalt = Encoding.UTF8.GetBytes("lockx.hello.v1");
    static readonly byte[] WrapAad = Encoding.UTF8.GetBytes("lockx.hello.wrap");

    public async Task<bool> IsAvailableAsync()
    {
        try { return await KeyCredentialManager.IsSupportedAsync(); }
        catch { return false; }
    }

    public async Task<DeviceWrap> EnrollAsync(string vaultId, byte[] masterKey)
    {
        var name = "LockX-" + vaultId;
        var created = await KeyCredentialManager.RequestCreateAsync(name, KeyCredentialCreationOption.ReplaceExisting);
        ThrowIfFailed(created.Status);

        var challenge = Kdf.RandomBytes(32);
        var kek = await DeriveKekAsync(created.Credential, challenge, name);
        try
        {
            return new DeviceWrap
            {
                KeyName = name,
                Challenge = challenge,
                SealedMasterKey = Sealed.Seal(masterKey, kek, WrapAad),
            };
        }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    public async Task<byte[]> UnwrapAsync(DeviceWrap wrap)
    {
        var opened = await KeyCredentialManager.OpenAsync(wrap.KeyName);
        ThrowIfFailed(opened.Status);

        var kek = await DeriveKekAsync(opened.Credential, wrap.Challenge, wrap.KeyName);
        try { return Sealed.Open(wrap.SealedMasterKey, kek, WrapAad); }
        catch (CryptoException) { throw new CryptoException("Windows Hello could not unlock this vault. Use your password."); }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    public async Task RemoveAsync(DeviceWrap wrap)
    {
        await KeyCredentialManager.DeleteAsync(wrap.KeyName);
    }

    static async Task<byte[]> DeriveKekAsync(KeyCredential credential, byte[] challenge, string name)
    {
        var signed = await credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(challenge));
        ThrowIfFailed(signed.Status);
        CryptographicBuffer.CopyToByteArray(signed.Result, out byte[] signature);
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, signature, 32, HkdfSalt, Encoding.UTF8.GetBytes(name));
        }
        finally { CryptographicOperations.ZeroMemory(signature); }
    }

    static void ThrowIfFailed(KeyCredentialStatus status)
    {
        switch (status)
        {
            case KeyCredentialStatus.Success:
                return;
            case KeyCredentialStatus.UserCanceled:
                throw new UnlockCancelledException();
            case KeyCredentialStatus.NotFound:
                throw new CryptoException("The Windows Hello key for this vault was not found. Use your password.");
            case KeyCredentialStatus.UserPrefersPassword:
                throw new UnlockCancelledException();
            default:
                throw new CryptoException($"Windows Hello is unavailable ({status}). Set up a PIN, face or fingerprint in Windows Settings.");
        }
    }
}
