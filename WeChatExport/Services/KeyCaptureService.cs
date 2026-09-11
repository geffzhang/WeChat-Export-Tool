using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace WeChatExport.Services;

/// <summary>How a capture attempt ended.</summary>
public enum KeyCaptureOutcome
{
    Captured,

    /// <summary><c>wx_key.dll</c> is not beside the executable.</summary>
    DllMissing,

    /// <summary>The user dismissed the UAC prompt.</summary>
    ElevationDenied,

    /// <summary>The DLL ran but could not install its hook.</summary>
    HookFailed,

    /// <summary>WeChat never closed, never started, or never handed over a key.</summary>
    TimedOut,

    Cancelled,

    Error,
}

/// <summary>The result of one capture attempt.</summary>
public sealed record KeyCaptureResult(
    KeyCaptureOutcome Outcome,
    string? Key,
    string? ImageKeyJson,
    string Message)
{
    public bool IsSuccess => Outcome == KeyCaptureOutcome.Captured;
}

/// <summary>
/// Captures WeChat's database key by running
/// <see cref="ElevatedKeyCapture"/> in an elevated child process and relaying
/// its progress to the UI.
///
/// The split exists because the hook in <c>wx_key.dll</c> requires administrator
/// rights, while the application itself is deliberately <c>asInvoker</c> (see
/// <c>app.manifest</c>). So rather than elevating the whole GUI - which would
/// run the entire app, including all its file and export work, with
/// administrator rights it does not need - this relaunches only the capture
/// step through the shell's <c>runas</c> verb. The user sees one UAC prompt and
/// the GUI keeps running unelevated.
///
/// This mirrors the original Python tool, which elevated <c>node.exe</c> to run
/// <c>get_key.js</c>. Here the elevated process is this same executable running
/// headless, so the Node sidecar is gone but the privilege boundary is
/// identical.
/// </summary>
public class KeyCaptureService
{
    /// <summary>
    /// Process names WeChat runs under. WeChat 4.x - the version whose data
    /// layout this app targets - runs as <c>Weixin.exe</c>; older builds ran as
    /// <c>WeChat.exe</c>. Checking only "WeChat" reported "not running" on
    /// exactly the machines this app is for.
    /// </summary>
    private static readonly string[] WeChatProcessNames = { "WeChat", "Weixin" };

    /// <summary>Win32 "the operation was cancelled by the user" - a dismissed UAC prompt.</summary>
    private const int ErrorCancelled = 1223;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Ceiling on how long the parent waits for the child. The child's own
    /// phases total at most 200s; this is the backstop for a child that dies
    /// without writing a final status, so the UI can never wait forever.
    /// </summary>
    private static readonly TimeSpan ChildTimeout = TimeSpan.FromSeconds(240);

    private readonly string _keyStoragePath;
    private readonly string _imageKeyStoragePath;
    private readonly ILogger _logger;

    public KeyCaptureService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var configDir = Path.Combine(appData, "WeChatExport");
        Directory.CreateDirectory(configDir);
        _keyStoragePath = Path.Combine(configDir, "wechat_key.json");
        _imageKeyStoragePath = Path.Combine(configDir, "image_key.json");
        _logger = Log.ForContext<KeyCaptureService>();
    }

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
    /// Runs one capture attempt, reporting human-readable progress as it goes.
    ///
    /// The caller should tell the user to close WeChat first: the hook can only
    /// observe the key if it is installed before WeChat starts, because WeChat
    /// 4.x calls <c>SetDBKey</c> once, at process start.
    /// </summary>
    public async Task<KeyCaptureResult> CaptureKeyAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _logger.Information("Attempting to capture WeChat decryption key");

        var sessionDirectory = KeyCaptureProtocol.NewSessionDirectory();
        var statusPath = Path.Combine(sessionDirectory, KeyCaptureProtocol.StatusFileName);
        var keyPath = Path.Combine(sessionDirectory, KeyCaptureProtocol.KeyFileName);
        var imageKeyPath = Path.Combine(sessionDirectory, KeyCaptureProtocol.ImageKeyFileName);

        try
        {
            using var child = StartElevatedChild(sessionDirectory);
            if (child is null)
            {
                return new KeyCaptureResult(
                    KeyCaptureOutcome.ElevationDenied,
                    null,
                    null,
                    "Administrator access is required to capture the key. The request was declined.");
            }

            _logger.Information("Elevated capture process started (pid {Pid})", child.Id);

            var lastStatus = KeyCaptureProtocol.Started;
            var deadline = DateTime.UtcNow + ChildTimeout;

            while (DateTime.UtcNow < deadline)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    // The elevated child cannot be killed from here (an
                    // unelevated process has no rights over it), so it is left to
                    // reach its own timeout. It holds no lock and writes only in
                    // its own session directory.
                    _logger.Information("Capture cancelled by the user");
                    return new KeyCaptureResult(
                        KeyCaptureOutcome.Cancelled, null, null, "Key capture cancelled.");
                }

                if (TryReadKey(keyPath, out var key))
                {
                    var imageKey = TryReadText(imageKeyPath);
                    _logger.Information("Captured database key ({Prefix}...)", key[..16]);

                    SaveKey(key);
                    if (!string.IsNullOrWhiteSpace(imageKey))
                        SaveImageKey(imageKey);

                    progress?.Report("Key captured.");
                    return new KeyCaptureResult(
                        KeyCaptureOutcome.Captured,
                        key,
                        imageKey,
                        "Decryption key captured from WeChat.");
                }

                var status = TryReadText(statusPath);
                if (!string.IsNullOrWhiteSpace(status) && status != lastStatus)
                {
                    lastStatus = status;
                    var (code, detail) = KeyCaptureProtocol.Parse(status);
                    var text = KeyCaptureProtocol.Describe(code);

                    if (text is not null)
                        progress?.Report(detail is null ? text : $"{text} ({detail})");

                    _logger.Information("Capture status: {Status}", status);

                    // Terminal statuses the child writes before exiting. Acting on
                    // them immediately reports the real reason instead of waiting
                    // out the timeout for a process that has already given up.
                    if (code is KeyCaptureProtocol.DllNotFound)
                        return new KeyCaptureResult(
                            KeyCaptureOutcome.DllMissing, null, null,
                            "wx_key.dll was not found beside the application.");

                    if (code is KeyCaptureProtocol.HookFailed)
                        return new KeyCaptureResult(
                            KeyCaptureOutcome.HookFailed, null, null,
                            $"Could not hook WeChat: {detail ?? "unknown reason"}");

                    if (code is KeyCaptureProtocol.TimeoutClose
                        or KeyCaptureProtocol.TimeoutStart
                        or KeyCaptureProtocol.TimeoutPoll)
                        return new KeyCaptureResult(
                            KeyCaptureOutcome.TimedOut, null, null,
                            text ?? "Key capture timed out.");
                }

                if (child.HasExited && !TryReadKey(keyPath, out _))
                {
                    // Exited without producing a key and without a status we
                    // already handled - report the last thing it said rather than
                    // a bare failure.
                    var (code, _) = KeyCaptureProtocol.Parse(lastStatus);
                    return new KeyCaptureResult(
                        KeyCaptureOutcome.Error, null, null,
                        $"Key capture stopped unexpectedly (last status: {KeyCaptureProtocol.Describe(code) ?? lastStatus}).");
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }

            _logger.Warning("Timed out waiting for the elevated capture process");
            return new KeyCaptureResult(
                KeyCaptureOutcome.TimedOut, null, null,
                "Timed out waiting for the key. Close WeChat, then try again and open WeChat when prompted.");
        }
        catch (OperationCanceledException)
        {
            return new KeyCaptureResult(
                KeyCaptureOutcome.Cancelled, null, null, "Key capture cancelled.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to capture key");
            return new KeyCaptureResult(
                KeyCaptureOutcome.Error, null, null, $"Key capture failed: {ex.Message}");
        }
        finally
        {
            CleanupSession(sessionDirectory);
        }
    }

    /// <summary>
    /// Relaunches this executable elevated, running only the capture step.
    /// Returns null when the user declines the UAC prompt, which is a normal
    /// outcome rather than an error.
    /// </summary>
    private Process? StartElevatedChild(string sessionDirectory)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            _logger.Error("Cannot determine the current executable path to elevate");
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = $"{KeyCaptureProtocol.CaptureSwitch} \"{sessionDirectory}\"",
            UseShellExecute = true,
            Verb = "runas",
            // The child is a console-less GUI binary; it must not flash a window.
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            var process = Process.Start(startInfo);
            if (process is null)
            {
                _logger.Error("Elevated process did not start");
                return null;
            }

            return process;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            _logger.Information("User declined the elevation prompt");
            return null;
        }
        catch (Win32Exception ex)
        {
            _logger.Error(ex, "Could not start the elevated capture process");
            return null;
        }
    }

    /// <summary>
    /// Reads the captured key, retrying past the moment the child may still be
    /// writing it. Only a well-formed key is accepted, so a partially written
    /// file is retried rather than used.
    /// </summary>
    private static bool TryReadKey(string path, out string key)
    {
        key = string.Empty;
        var text = TryReadText(path);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = text.Trim();
        if (text.Length != 64)
            return false;

        foreach (var c in text)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }

        key = text;
        return true;
    }

    private static string? TryReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            // Being written right now; the next poll will pick it up.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The child runs elevated and creates the session directory, so it is owned
    /// by an administrator; an unelevated delete can fail. Leftovers are
    /// harmless - each attempt gets a fresh directory and the root is under the
    /// user's own config folder.
    /// </summary>
    private void CleanupSession(string sessionDirectory)
    {
        try
        {
            if (Directory.Exists(sessionDirectory))
                Directory.Delete(sessionDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Could not remove capture session directory {Directory}", sessionDirectory);
        }
    }

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

    /// <summary>
    /// Persists the image key beside the database key. Nothing consumes it yet -
    /// image decryption is a separate, still-missing capability - but capturing
    /// it costs nothing extra here and it cannot be obtained again without
    /// another WeChat restart.
    /// </summary>
    public void SaveImageKey(string json)
    {
        try
        {
            File.WriteAllText(_imageKeyStoragePath, json);
            _logger.Information("Image key saved successfully");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save image key");
        }
    }

    public string? LoadImageKey()
    {
        try
        {
            return File.Exists(_imageKeyStoragePath)
                ? File.ReadAllText(_imageKeyStoragePath)
                : null;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load image key");
            return null;
        }
    }
}
