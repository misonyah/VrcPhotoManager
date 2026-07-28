using System.Security.Cryptography;
using System.Text;
using VrcPhotoManager.Data;

namespace VrcPhotoManager.Services;

/// <summary>
/// Encrypts the VRCDN session cookie with Windows DPAPI (tied to the current Windows
/// account, no password needed) by default. If the user sets a password, an additional
/// AES layer (key derived via PBKDF2) wraps the DPAPI blob before it's persisted, so even
/// another process running as the same Windows user can't read it without the password too.
/// </summary>
public class CredentialStore
{
    private const string CookieKey = "vrcdn_session_cookie";
    private const string PasswordSaltKey = "password_salt";
    private const int Pbkdf2Iterations = 200_000;

    private readonly PhotoRepository _repo;

    public CredentialStore(PhotoRepository repo)
    {
        _repo = repo;
    }

    public bool IsPasswordProtected => _repo.GetSetting(PasswordSaltKey) is not null;

    public void SaveCookie(string cookieValue, string? password)
    {
        byte[] plain = Encoding.UTF8.GetBytes(cookieValue);
        byte[] dpapiProtected = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);

        if (string.IsNullOrEmpty(password))
        {
            _repo.SetSetting(CookieKey, dpapiProtected);
            return;
        }

        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        byte[] encrypted = AesEncrypt(dpapiProtected, key);

        _repo.SetSetting(PasswordSaltKey, salt);
        _repo.SetSetting(CookieKey, encrypted);
    }

    public string? LoadCookie(string? password)
    {
        byte[]? stored = _repo.GetSetting(CookieKey);
        if (stored is null) return null;

        byte[]? salt = _repo.GetSetting(PasswordSaltKey);
        byte[] dpapiProtected;
        if (salt is not null)
        {
            if (string.IsNullOrEmpty(password))
                throw new InvalidOperationException("This credential is password-protected; a password is required.");
            byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
            dpapiProtected = AesDecrypt(stored, key);
        }
        else
        {
            dpapiProtected = stored;
        }

        byte[] plain = ProtectedData.Unprotect(dpapiProtected, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] AesEncrypt(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();
        using var encryptor = aes.CreateEncryptor();
        byte[] cipher = encryptor.TransformFinalBlock(data, 0, data.Length);
        return [.. aes.IV, .. cipher];
    }

    private static byte[] AesDecrypt(byte[] ivAndCipher, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        byte[] iv = ivAndCipher[..16];
        byte[] cipher = ivAndCipher[16..];
        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
    }
}
