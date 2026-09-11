using System;
using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace WeChatExport.Core.Decryption;

/// <summary>
/// Provides cryptographic utilities for WeChat WCDB database decryption.
/// </summary>
public static class CryptoUtils
{
    private static readonly ILogger Logger = Log.ForContext(typeof(CryptoUtils));

    /// <summary>
    /// Derives a 32-byte key using PBKDF2 with the WeChat-specific salt.
    /// </summary>
    /// <param name="password">The password/key to derive from (typically mobile device ID or custom key).</param>
    /// <param name="salt">Optional salt bytes. If null, uses WeChat default salt.</param>
    /// <returns>Derived 32-byte key for AES-256 encryption.</returns>
    public static byte[] DeriveKey(string password, byte[]? salt = null)
    {
        salt ??= GetWeChatDefaultSalt();

        return Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            10000,
            HashAlgorithmName.SHA1,
            32);
    }

    /// <summary>
    /// Gets the WeChat default salt used in WCDB encryption.
    /// </summary>
    /// <remarks>
    /// NOT a SQLCipher salt, and not usable as one: this is the literal string
    /// "wxsecdbkey". SQLCipher's salt is the database file's own first 16 bytes
    /// (see DatabaseService.ReadSalt). Do not route this into the live decryption
    /// path.
    /// </remarks>
    private static byte[] GetWeChatDefaultSalt()
    {
        // WeChat WCDB default salt - typically derived from device information
        return Encoding.UTF8.GetBytes("wxsecdbkey");
    }

    /// <summary>
    /// Decrypts data using AES-256-CBC.
    /// </summary>
    /// <param name="encryptedData">The encrypted data (including IV at the beginning).</param>
    /// <param name="key">32-byte encryption key.</param>
    /// <returns>Decrypted data.</returns>
    public static byte[] DecryptAes256Cbc(byte[] encryptedData, byte[] key)
    {
        if (encryptedData == null || encryptedData.Length < 16)
        {
            throw new ArgumentException("Encrypted data must be at least 16 bytes (IV + ciphertext)");
        }

        if (key.Length != 32)
        {
            throw new ArgumentException("Key must be 32 bytes for AES-256");
        }

        // Extract IV from the first 16 bytes
        byte[] iv = new byte[16];
        Array.Copy(encryptedData, 0, iv, 0, 16);

        // Extract ciphertext (everything after the IV)
        byte[] ciphertext = new byte[encryptedData.Length - 16];
        Array.Copy(encryptedData, 16, ciphertext, 0, ciphertext.Length);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }

    /// <summary>
    /// Encrypts data using AES-256-CBC.
    /// </summary>
    /// <param name="plainData">The plaintext data to encrypt.</param>
    /// <param name="key">32-byte encryption key.</param>
    /// <returns>Encrypted data with IV prepended.</returns>
    public static byte[] EncryptAes256Cbc(byte[] plainData, byte[] key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("Key must be 32 bytes for AES-256");
        }

        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        byte[] ciphertext = encryptor.TransformFinalBlock(plainData, 0, plainData.Length);

        // Prepend IV to ciphertext
        byte[] result = new byte[16 + ciphertext.Length];
        Array.Copy(aes.IV, 0, result, 0, 16);
        Array.Copy(ciphertext, 0, result, 16, ciphertext.Length);

        return result;
    }

    /// <summary>
    /// Computes SHA1 hash of the input data.
    /// </summary>
    public static byte[] ComputeSha1(byte[] data)
    {
        using var sha1 = SHA1.Create();
        return sha1.ComputeHash(data);
    }

    /// <summary>
    /// Computes MD5 hash of the input data.
    /// </summary>
    public static byte[] ComputeMd5(byte[] data)
    {
        using var md5 = MD5.Create();
        return md5.ComputeHash(data);
    }

    /// <summary>
    /// Computes MD5 hash of the input string.
    /// </summary>
    public static string ComputeMd5String(string input)
    {
        using var md5 = MD5.Create();
        byte[] hashBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// Validates whether a string could be a valid WeChat decryption key.
    /// </summary>
    public static bool IsValidKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        // Key should be hex-encoded (32 bytes = 64 hex chars) or raw string
        if (key.Length == 64 && IsHexString(key))
            return true;

        // Or could be any non-empty string used as password for key derivation
        return key.Length >= 8;
    }

    /// <summary>
    /// Normalises a user-supplied WeChat decryption key into the 64-character
    /// lowercase hex form used for WeChat's 32 bytes of key material.
    /// </summary>
    /// <remarks>
    /// This is <em>key material</em>, not a finished SQLCipher key. WeChat hands
    /// these 32 bytes to <c>sqlite3_key()</c>, and SQLCipher then runs its own
    /// PBKDF2 over them, salted with the database file's first 16 bytes. The bytes
    /// returned here therefore still have to be derived before SQLCipher can use
    /// them: <c>x'&lt;these 64 hex chars&gt;'</c> is SQLCipher's <em>raw key</em>
    /// form and BYPASSES the KDF entirely, so passing it directly cannot open a
    /// database WeChat created. See DatabaseService.Connect for the derivation and
    /// the per-version candidate parameters.
    /// </remarks>
    /// <param name="key">The user-supplied key (may have surrounding whitespace or a 0x prefix).</param>
    /// <param name="normalizedHex">The 64-character lowercase hex key material when this returns true.</param>
    /// <returns>True if <paramref name="key"/> is 64 hex characters of WeChat key material.</returns>
    public static bool TryNormalizeHexKey(string? key, out string? normalizedHex)
    {
        normalizedHex = null;

        if (string.IsNullOrWhiteSpace(key))
            return false;

        var candidate = key.Trim();

        // Tolerate the "0x" prefix some key-extraction tools print.
        if (candidate.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            candidate = candidate[2..];

        // A WeChat raw key is exactly 32 bytes == 64 hex chars.
        if (candidate.Length != 64 || !IsHexString(candidate))
            return false;

        normalizedHex = candidate.ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// Checks if a string is a valid hex string.
    /// </summary>
    public static bool IsHexString(string s)
    {
        foreach (char c in s)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Converts hex string to byte array.
    /// </summary>
    public static byte[] HexToBytes(string hex)
    {
        if (hex == null)
            throw new ArgumentNullException(nameof(hex));

        if (hex.Length % 2 != 0)
            throw new ArgumentException("Hex string must have even length");

        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    /// <summary>
    /// Converts byte array to hex string.
    /// </summary>
    public static string BytesToHex(byte[] bytes)
    {
        return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// Attempts to decrypt with multiple key strategies.
    /// </summary>
    public static byte[]? TryDecryptWithStrategies(byte[] encryptedData, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            Logger.Warning("No key provided for decryption");
            return null;
        }

        try
        {
            // Strategy 1: Direct hex key
            if (key.Length == 64 && IsHexString(key))
            {
                var keyBytes = HexToBytes(key);
                return DecryptAes256Cbc(encryptedData, keyBytes);
            }

            // Strategy 2: Key as password (derive)
            var derivedKey = DeriveKey(key);
            return DecryptAes256Cbc(encryptedData, derivedKey);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Decryption failed with provided key");
            return null;
        }
    }
}
