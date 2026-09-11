using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Serilog;

namespace WeChatExport.Core.Decryption;

/// <summary>
/// Handles decryption of WeChat WCDB (WeChat Database) encrypted files.
/// </summary>
public class WeChatDecryptor
{
    private static readonly ILogger Logger = Log.ForContext<WeChatDecryptor>();

    // WCDB file magic number: "WCDB" in ASCII
    private static readonly byte[] WcdbMagic = Encoding.ASCII.GetBytes("WCDB");

    // WCDB encrypted file header flags
    private const byte WcdbFlagEncrypted = 0x01;
    private const byte WcdbFlagCompressed = 0x02;

    private readonly string? _decryptionKey;

    public WeChatDecryptor(string? decryptionKey = null)
    {
        _decryptionKey = decryptionKey;
    }

    /// <summary>
    /// Checks if a file is a WCDB encrypted database.
    /// </summary>
    public static bool IsWcdbFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return false;

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            if (fs.Length < 16)
                return false;

            byte[] header = new byte[16];
            fs.ReadExactly(header, 0, 16);

            // Check for WCDB magic bytes
            return header.Take(4).SequenceEqual(WcdbMagic);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error checking if file is WCDB: {FilePath}", filePath);
            return false;
        }
    }

    /// <summary>
    /// Checks if a file is encrypted based on its header.
    /// </summary>
    public static bool IsEncrypted(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            if (fs.Length < 16)
                return false;

            byte[] header = new byte[16];
            fs.ReadExactly(header, 0, 16);

            // WCDB header: "WCDB" (4 bytes) + version (4 bytes) + flags (4 bytes) + ...
            // Check the flags byte at offset 12
            if (!header.Take(4).SequenceEqual(WcdbMagic))
                return false;

            // Flags are at offset 12 (after magic + version)
            byte flags = header[12];
            return (flags & WcdbFlagEncrypted) != 0;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error checking if file is encrypted: {FilePath}", filePath);
            return false;
        }
    }

    /// <summary>
    /// Decrypts a WCDB encrypted file.
    /// </summary>
    /// <param name="inputPath">Path to the encrypted WCDB file.</param>
    /// <param name="outputPath">Path to save the decrypted file.</param>
    /// <param name="key">Decryption key. Uses constructor key if not provided.</param>
    /// <returns>True if decryption was successful.</returns>
    public bool DecryptFile(string inputPath, string outputPath, string? key = null)
    {
        var decryptionKey = key ?? _decryptionKey;

        if (string.IsNullOrWhiteSpace(decryptionKey))
        {
            Logger.Error("No decryption key provided");
            return false;
        }

        try
        {
            Logger.Information("Starting decryption of {InputPath}", inputPath);

            byte[] fileData = File.ReadAllBytes(inputPath);
            byte[]? decryptedData = DecryptData(fileData, decryptionKey);

            if (decryptedData == null)
            {
                Logger.Error("Decryption failed - invalid key or corrupted file");
                return false;
            }

            // Ensure output directory exists
            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            File.WriteAllBytes(outputPath, decryptedData);
            Logger.Information("Decryption completed successfully: {OutputPath}", outputPath);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error decrypting file: {InputPath}", inputPath);
            return false;
        }
    }

    /// <summary>
    /// Decrypts WCDB data in memory.
    /// </summary>
    /// <param name="encryptedData">The encrypted WCDB data.</param>
    /// <param name="key">Decryption key.</param>
    /// <returns>Decrypted data or null if decryption fails.</returns>
    public byte[]? DecryptData(byte[] encryptedData, string key)
    {
        if (encryptedData == null || encryptedData.Length < 32)
        {
            Logger.Error("Invalid encrypted data - too short");
            return null;
        }

        // Verify WCDB magic
        byte[] header = encryptedData.Take(16).ToArray();
        if (!header.Take(4).SequenceEqual(WcdbMagic))
        {
            Logger.Error("Not a valid WCDB file");
            return null;
        }

        // Check if encrypted
        byte flags = header[12];
        if ((flags & WcdbFlagEncrypted) == 0)
        {
            Logger.Warning("File is not encrypted, returning as-is");
            return encryptedData;
        }

        try
        {
            // WCDB encrypted data structure:
            // Header: WCDB (4) + version (4) + flags (4) + key_check (4) + salt (16) + ...
            // Encrypted payload starts after header (typically offset 44 or later)

            // Extract salt from header (offset 16)
            byte[] salt = new byte[16];
            Array.Copy(encryptedData, 16, salt, 0, 16);

            // Extract key check value (offset 12) - used to validate key
            byte[] keyCheck = new byte[4];
            Array.Copy(encryptedData, 12, keyCheck, 0, 4);

            // Try to decrypt
            // The actual encrypted content starts after the header
            // Standard WCDB header is 44 bytes
            const int headerSize = 44;

            if (encryptedData.Length <= headerSize)
            {
                Logger.Error("Encrypted data too short");
                return null;
            }

            // Get encrypted content (after header)
            byte[] encryptedContent = new byte[encryptedData.Length - headerSize];
            Array.Copy(encryptedData, headerSize, encryptedContent, 0, encryptedContent.Length);

            // Try multiple decryption strategies
            byte[]? decryptedContent = TryDecryptContent(encryptedContent, key, salt);

            if (decryptedContent == null)
            {
                return null;
            }

            // Reconstruct the file with decrypted content
            byte[] result = new byte[headerSize + decryptedContent.Length];
            Array.Copy(encryptedData, 0, result, 0, headerSize);

            // Update flags to indicate decrypted
            result[12] = (byte)(flags & ~WcdbFlagEncrypted);

            Array.Copy(decryptedContent, 0, result, headerSize, decryptedContent.Length);

            return result;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error decrypting WCDB data");
            return null;
        }
    }

    /// <summary>
    /// Attempts to decrypt content with various key formats.
    /// </summary>
    private byte[]? TryDecryptContent(byte[] encryptedContent, string key, byte[] salt)
    {
        // Strategy 1: Key as direct 32-byte hex
        if (key.Length == 64 && CryptoUtils.IsHexString(key))
        {
            try
            {
                var keyBytes = CryptoUtils.HexToBytes(key);
                return CryptoUtils.DecryptAes256Cbc(encryptedContent, keyBytes);
            }
            catch
            {
                Logger.Debug("Direct hex key failed");
            }
        }

        // Strategy 2: Key as password with salt derivation
        try
        {
            var derivedKey = CryptoUtils.DeriveKey(key, salt);
            return CryptoUtils.DecryptAes256Cbc(encryptedContent, derivedKey);
        }
        catch
        {
            Logger.Debug("Key derivation with salt failed");
        }

        // Strategy 3: Key as password with default salt
        try
        {
            var derivedKey = CryptoUtils.DeriveKey(key, null);
            return CryptoUtils.DecryptAes256Cbc(encryptedContent, derivedKey);
        }
        catch
        {
            Logger.Debug("Key derivation with default salt failed");
        }

        // Strategy 4: Try as raw 32-byte key
        try
        {
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            if (keyBytes.Length == 32)
            {
                return CryptoUtils.DecryptAes256Cbc(encryptedContent, keyBytes);
            }
            else if (keyBytes.Length >= 32)
            {
                byte[] truncatedKey = new byte[32];
                Array.Copy(keyBytes, truncatedKey, 32);
                return CryptoUtils.DecryptAes256Cbc(encryptedContent, truncatedKey);
            }
        }
        catch
        {
            Logger.Debug("Raw key attempt failed");
        }

        Logger.Warning("All decryption strategies failed");
        return null;
    }

    /// <summary>
    /// Gets the list of WCDB database files in a directory.
    /// </summary>
    public static IEnumerable<string> FindWcdbFiles(string basePath, bool recursive = true)
    {
        if (!Directory.Exists(basePath))
        {
            Logger.Warning("Directory does not exist: {Path}", basePath);
            yield break;
        }

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        string[] patterns = { "*.db", "*.db-shm", "*.db-wal" };

        foreach (var pattern in patterns)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(basePath, pattern, searchOption);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error enumerating files with pattern {Pattern}", pattern);
                continue;
            }

            foreach (var file in files)
            {
                if (IsWcdbFile(file))
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>
    /// Batch decrypt multiple WCDB files.
    /// </summary>
    public Dictionary<string, bool> DecryptBatch(
        IEnumerable<string> inputFiles,
        string outputDirectory,
        string? key = null)
    {
        var results = new Dictionary<string, bool>();
        var decryptionKey = key ?? _decryptionKey;

        if (string.IsNullOrWhiteSpace(decryptionKey))
        {
            Logger.Error("No decryption key provided for batch operation");
            return results;
        }

        Directory.CreateDirectory(outputDirectory);

        foreach (var inputFile in inputFiles)
        {
            try
            {
                string fileName = Path.GetFileName(inputFile);
                string outputPath = Path.Combine(outputDirectory, fileName);

                bool success = DecryptFile(inputFile, outputPath, decryptionKey);
                results[inputFile] = success;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error processing file {File}", inputFile);
                results[inputFile] = false;
            }
        }

        return results;
    }
}
