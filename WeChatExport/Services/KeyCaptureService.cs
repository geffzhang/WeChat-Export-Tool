using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Serilog;

namespace WeChatExport.Services;

public class KeyCaptureService
{
    private readonly string _keyStoragePath;
    private readonly ILogger _logger;

    public KeyCaptureService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var configDir = Path.Combine(appData, "WeChatExport");
        Directory.CreateDirectory(configDir);
        _keyStoragePath = Path.Combine(configDir, "wechat_key.json");
        _logger = Log.ForContext<KeyCaptureService>();
    }

    public bool IsWeChatRunning()
    {
        return Process.GetProcessesByName("WeChat").Length > 0;
    }

    public string? CaptureKeyFromProcess()
    {
        _logger.Information("Attempting to capture WeChat decryption key");

        try
        {
            var wechatProcesses = Process.GetProcessesByName("WeChat");
            if (wechatProcesses.Length == 0)
            {
                _logger.Warning("WeChat process not found");
                return null;
            }

            // Key capture logic - search process memory for known patterns
            // This is a simplified version - actual implementation requires
            // more sophisticated memory scanning
            var process = wechatProcesses[0];

            // TODO: Implement actual key capture from process memory
            // The key is typically stored in a specific memory region
            // Known patterns: specific strings or byte sequences

            _logger.Warning("Key capture not fully implemented - manual entry required");
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to capture key from WeChat process");
            return null;
        }
    }

    public void SaveKey(string key)
    {
        try
        {
            var keyData = new { Key = key, SavedAt = DateTime.Now };
            var json = JsonSerializer.Serialize(keyData);
            File.WriteAllText(_keyStoragePath, json);
            _logger.Information("Key saved successfully");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save key");
        }
    }

    public string? LoadKey()
    {
        try
        {
            if (!File.Exists(_keyStoragePath))
                return null;

            var json = File.ReadAllText(_keyStoragePath);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("Key").GetString();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load key");
            return null;
        }
    }
}
