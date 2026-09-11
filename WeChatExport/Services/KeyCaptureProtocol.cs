using System;
using System.IO;

namespace WeChatExport.Services;

/// <summary>
/// The contract between the two processes involved in capturing WeChat's key.
///
/// The hook in <c>wx_key.dll</c> can only be installed from an elevated process,
/// so the GUI relaunches itself with <c>--capture-key</c>, and that second,
/// elevated instance does the work. The two communicate through files in a
/// per-attempt directory: the child writes its progress to
/// <c>status.txt</c> and the key to <c>key.txt</c>; the parent polls for both.
///
/// Files are used rather than a pipe deliberately. The original Python tool did
/// the same, and it keeps the two processes independent across the UAC boundary
/// - the parent stays responsive and can report progress live, and a child that
/// is killed (or that the user dismisses UAC out of) simply stops writing
/// rather than tearing down a shared channel.
///
/// <para>
/// <b>Trust.</b> The child runs elevated and writes into a directory the
/// unelevated user can also write. That is not a new exposure: the key is
/// already persisted in plaintext under the same user's <c>%APPDATA%</c> by
/// <see cref="KeyCaptureService.SaveKey"/>. The child only ever <i>writes</i>
/// here and never reads anything it did not create, so there is nothing for a
/// low-privilege process to tamper with that would matter.
/// </para>
/// </summary>
public static class KeyCaptureProtocol
{
    public const string CaptureSwitch = "--capture-key";

    public const string StatusFileName = "status.txt";
    public const string KeyFileName = "key.txt";
    public const string ImageKeyFileName = "image_key.json";

    /// <summary>
    /// A fresh directory for one capture attempt. Each attempt gets its own, so
    /// a stale <c>key.txt</c> from an earlier run can never be mistaken for this
    /// one's result - the bug the original guarded against by deleting the file
    /// up front, which races if two attempts overlap.
    /// </summary>
    public static string NewSessionDirectory()
    {
        var root = Path.Combine(RootDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// Where capture attempts live. Kept under the app's own config directory
    /// rather than the Desktop, so an elevated process is not writing loose
    /// files into the user's shell folders.
    /// </summary>
    public static string RootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WeChatExport",
        "keycapture");

    // ── Status codes ─────────────────────────────────────────────────────────
    // The same vocabulary the Python tool's status_map used, so the two behave
    // identically from the user's side. `hook_failed` and `error` carry a
    // detail suffix after a colon.

    public const string Started = "started";
    public const string DllFound = "dll_found";
    public const string DllNotFound = "dll_not_found";
    public const string DllLoaded = "dll_loaded";
    public const string WaitingClose = "waiting_close";
    public const string TimeoutClose = "timeout_close";
    public const string WaitingStart = "waiting_start";
    public const string TimeoutStart = "timeout_start";
    public const string Injecting = "injecting";
    public const string HookOk = "hook_ok";
    public const string HookFailed = "hook_failed";
    public const string Polling = "polling";
    public const string TimeoutPoll = "timeout_poll";
    public const string Captured = "captured";
    public const string Error = "error";

    /// <summary>
    /// Splits a status line into its code and optional detail (the part after
    /// the first colon).
    /// </summary>
    public static (string Code, string? Detail) Parse(string status)
    {
        var separator = status.IndexOf(':');
        return separator < 0
            ? (status.Trim(), null)
            : (status[..separator].Trim(), status[(separator + 1)..].Trim());
    }

    /// <summary>
    /// Human-readable progress text for a status code, or null if the code is
    /// not one we know (in which case callers should show the raw code rather
    /// than silently displaying nothing).
    /// </summary>
    public static string? Describe(string code) => code switch
    {
        Started => "Capturing key...",
        DllFound => "Found wx_key.dll",
        DllNotFound => "wx_key.dll is missing from the application folder",
        DllLoaded => "Loaded wx_key.dll",
        WaitingClose => "Waiting for WeChat to close - please quit WeChat from the system tray",
        TimeoutClose => "WeChat did not close in time",
        WaitingStart => "Waiting for WeChat to start - open WeChat now and sign in",
        TimeoutStart => "WeChat did not start in time",
        Injecting => "Injecting hook...",
        HookOk => "Hook installed - waiting for the key",
        HookFailed => "Failed to install the hook",
        Polling => "Waiting for WeChat to sign in and hand over the key...",
        TimeoutPoll => "Timed out waiting for the key",
        Captured => "Key captured",
        Error => "Capture failed",
        _ => null,
    };
}
