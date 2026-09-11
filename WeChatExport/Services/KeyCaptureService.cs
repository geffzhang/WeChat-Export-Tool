using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Serilog;

namespace WeChatExport.Services;

public class KeyCaptureService
{
    /// <summary>
    /// Process names WeChat runs under. WeChat 4.x - the version whose data layout
    /// this app targets - runs as <c>Weixin.exe</c>; older builds ran as
    /// <c>WeChat.exe</c>. Checking only "WeChat" reported "not running" on exactly
    /// the machines this app is for.
    /// </summary>
    private static readonly string[] WeChatProcessNames = { "WeChat", "Weixin" };

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

    /// <summary>Result of a capture attempt: whether WeChat is up, and the key if we got one.</summary>
    public readonly record struct KeyCaptureAttempt(bool WeChatRunning, string? Key);

    /// <summary>
    /// Whether WeChat is running, checking every process name it ships under.
    /// </summary>
    public bool IsWeChatRunning()
    {
        var processes = GetWeChatProcesses();
        try
        {
            return processes.Count > 0;
        }
        finally
        {
            DisposeProcesses(processes);
        }
    }

    /// <summary>
    /// One enumeration of the WeChat processes, so a capture attempt can answer
    /// "is it running?" and "did we get a key?" without walking the process list
    /// twice. The process-memory scan is still a deliberate stub.
    /// </summary>
    public KeyCaptureAttempt AttemptCapture()
    {
        _logger.Information("Attempting to capture WeChat decryption key");

        var processes = GetWeChatProcesses();
        try
        {
            if (processes.Count == 0)
            {
                _logger.Warning(
                    "WeChat process not found (checked: {ProcessNames})",
                    string.Join(", ", WeChatProcessNames));
                return new KeyCaptureAttempt(false, null);
            }

            // Key capture logic - search process memory for known patterns
            // This is a simplified version - actual implementation requires
            // more sophisticated memory scanning

            // TODO: Implement actual key capture from process memory
            // The key is typically stored in a specific memory region
            // Known patterns: specific strings or byte sequences

            _logger.Warning("Key capture not fully implemented - manual entry required");
            return new KeyCaptureAttempt(true, null);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to capture key from WeChat process");
            return new KeyCaptureAttempt(true, null);
        }
        finally
        {
            DisposeProcesses(processes);
        }
    }

    /// <summary>
    /// Captures the key from the running WeChat process, if it can. Still a stub -
    /// always null today - but it no longer leaks process handles.
    /// </summary>
    public string? CaptureKeyFromProcess()
    {
        return AttemptCapture().Key;
    }

    /// <summary>
    /// Every running WeChat process. Callers must dispose the results:
    /// <see cref="Process.GetProcessesByName(string)"/> handles are never released
    /// otherwise.
    /// </summary>
    private static List<Process> GetWeChatProcesses()
    {
        var processes = new List<Process>();

        foreach (var processName in WeChatProcessNames)
        {
            try
            {
                processes.AddRange(Process.GetProcessesByName(processName));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to enumerate processes named {ProcessName}", processName);
            }
        }

        return processes;
    }

    private static void DisposeProcesses(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
            process.Dispose();
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
