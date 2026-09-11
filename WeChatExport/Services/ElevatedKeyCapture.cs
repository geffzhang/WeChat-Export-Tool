using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Serilog;
using WeChatExport.Core.Decryption;

namespace WeChatExport.Services;

/// <summary>
/// The child-process half of key capture: the code that runs <i>elevated</i>,
/// drives <c>wx_key.dll</c>, and reports progress through
/// <see cref="KeyCaptureProtocol"/>'s files.
///
/// This is a direct port of the original tool's <c>scripts/get_key.js</c>, with
/// Node removed. The phase structure, the timeouts and the status vocabulary are
/// deliberately identical to that script, because the behaviour they encode is
/// a property of WeChat, not of the tool:
///
/// <list type="number">
/// <item>WeChat 4.x calls <c>SetDBKey</c> exactly once, at process start - not
/// on sign-in and not on sign-out. A hook installed against an already-running
/// WeChat therefore never sees the key, which is why the user must restart
/// WeChat and why the hook goes in immediately after the new process appears.</item>
/// <item>The hook must be in place before <c>SetDBKey</c> runs, so it is
/// installed the moment the process is detected, not after it settles.</item>
/// <item>The key only arrives when the user actually signs in, which is
/// unbounded, so the poll window is generous (120s) and reports progress
/// meanwhile.</item>
/// </list>
/// </summary>
public static class ElevatedKeyCapture
{
    private static readonly string[] WeChatProcessNames = { "Weixin", "WeChat" };

    /// <summary>Matches the original: 40 x 500ms.</summary>
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(20);

    /// <summary>Matches the original: 120 x 500ms.</summary>
    private static readonly TimeSpan StartWait = TimeSpan.FromSeconds(60);

    /// <summary>Matches the original: 120s at a 200ms poll interval.</summary>
    private static readonly TimeSpan PollWait = TimeSpan.FromSeconds(120);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private static readonly Regex KeyShape = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    /// <summary>
    /// Runs one capture attempt. Returns a process exit code: 0 on success, 1
    /// on any failure (so the parent can distinguish "captured" from "did not",
    /// though it observes the same thing via the status file).
    /// </summary>
    public static int Run(string sessionDirectory)
    {
        Directory.CreateDirectory(sessionDirectory);
        var statusPath = Path.Combine(sessionDirectory, KeyCaptureProtocol.StatusFileName);
        var keyPath = Path.Combine(sessionDirectory, KeyCaptureProtocol.KeyFileName);
        var imageKeyPath = Path.Combine(sessionDirectory, KeyCaptureProtocol.ImageKeyFileName);

        void SetStatus(string status)
        {
            try
            {
                File.WriteAllText(statusPath, status);
            }
            catch (Exception ex)
            {
                // A status write failing must never abort the capture itself; the
                // parent simply keeps showing the previous line.
                Log.Debug(ex, "Could not write capture status {Status}", status);
            }
        }

        var log = Log.ForContext("Component", "ElevatedKeyCapture");
        log.Information("Key capture starting (session {Session})", sessionDirectory);
        SetStatus(KeyCaptureProtocol.Started);

        var hookInstalled = false;
        try
        {
            // ── Load the DLL ─────────────────────────────────────────────────
            if (!WxKeyInterop.IsAvailable())
            {
                SetStatus(KeyCaptureProtocol.DllNotFound);
                log.Error("wx_key.dll could not be loaded from {Base}", AppContext.BaseDirectory);
                return 1;
            }

            SetStatus(KeyCaptureProtocol.DllFound);
            SetStatus(KeyCaptureProtocol.DllLoaded);

            // ── Phase 1: make sure WeChat is closed ──────────────────────────
            if (TryFindWeChatPid(out _))
            {
                SetStatus(KeyCaptureProtocol.WaitingClose);
                if (!WaitForExit(ExitWait))
                {
                    SetStatus(KeyCaptureProtocol.TimeoutClose);
                    log.Warning("WeChat did not close within {Seconds}s", ExitWait.TotalSeconds);
                    return 1;
                }

                log.Information("WeChat closed");
            }

            // ── Phase 2: wait for it to come back ────────────────────────────
            SetStatus(KeyCaptureProtocol.WaitingStart);
            var pid = WaitForStart(StartWait);
            if (pid is null)
            {
                SetStatus(KeyCaptureProtocol.TimeoutStart);
                log.Warning("WeChat did not start within {Seconds}s", StartWait.TotalSeconds);
                return 1;
            }

            log.Information("Detected WeChat process {Pid}", pid.Value);

            // ── Phase 3: hook immediately, before SetDBKey is called ─────────
            SetStatus(KeyCaptureProtocol.Injecting);
            hookInstalled = WxKeyInterop.InitializeHook(pid.Value);
            if (!hookInstalled)
            {
                var reason = WxKeyInterop.LastError() ?? "no error reported by wx_key.dll";
                SetStatus($"{KeyCaptureProtocol.HookFailed}:{Truncate(reason, 60)}");
                log.Error("InitializeHook({Pid}) failed: {Reason}", pid.Value, reason);
                return 1;
            }

            log.Information("Hook installed against {Pid}", pid.Value);
            SetStatus(KeyCaptureProtocol.HookOk);

            // ── Phase 4: poll for the key ────────────────────────────────────
            SetStatus(KeyCaptureProtocol.Polling);
            var keyBuffer = new byte[WxKeyInterop.KeyBufferSize];
            var deadline = DateTime.UtcNow + PollWait;

            while (DateTime.UtcNow < deadline)
            {
                if (WxKeyInterop.PollKeyData(keyBuffer, keyBuffer.Length))
                {
                    var key = WxKeyInterop.DecodeBuffer(keyBuffer);
                    if (KeyShape.IsMatch(key))
                    {
                        File.WriteAllText(keyPath, key);
                        log.Information("Captured database key ({Prefix}...)", key[..16]);

                        CaptureImageKey(imageKeyPath, log);

                        SetStatus(KeyCaptureProtocol.Captured);
                        return 0;
                    }

                    log.Warning("PollKeyData returned a value that is not a 64-hex key; ignoring");
                }

                DrainStatusMessages(log);
                Thread.Sleep(PollInterval);
            }

            SetStatus(KeyCaptureProtocol.TimeoutPoll);
            log.Warning("No key within {Seconds}s", PollWait.TotalSeconds);
            return 1;
        }
        catch (Exception ex)
        {
            SetStatus($"{KeyCaptureProtocol.Error}:{Truncate(ex.Message, 80)}");
            log.Error(ex, "Key capture failed");
            return 1;
        }
        finally
        {
            if (hookInstalled)
            {
                try
                {
                    WxKeyInterop.CleanupHook();
                }
                catch (Exception ex)
                {
                    // The process is about to exit; a failed cleanup is not worth
                    // failing the run over, but it must not vanish silently.
                    log.Warning(ex, "CleanupHook failed");
                }
            }
        }
    }

    /// <summary>
    /// The image key is a separate secret, extracted through the same DLL and
    /// only available once the database key has been captured. Failing to get it
    /// does not fail the capture - the database key is the one everything else
    /// depends on.
    /// </summary>
    private static void CaptureImageKey(string path, ILogger log)
    {
        try
        {
            var buffer = new byte[WxKeyInterop.ImageKeyBufferSize];
            if (!WxKeyInterop.GetImageKey(buffer, buffer.Length))
            {
                log.Information("No image key available from wx_key.dll");
                return;
            }

            var json = WxKeyInterop.DecodeBuffer(buffer);
            if (string.IsNullOrWhiteSpace(json))
            {
                log.Information("Image key was empty; not saved");
                return;
            }

            File.WriteAllText(path, json);
            log.Information("Captured image key");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Image key capture failed");
        }
    }

    /// <summary>
    /// Drains the DLL's pending progress lines into the log. The DLL narrates
    /// what it is doing inside WeChat, and that narration is the only diagnostic
    /// available when a capture fails for a reason it does not report as an
    /// error.
    /// </summary>
    private static void DrainStatusMessages(ILogger log)
    {
        var buffer = new byte[WxKeyInterop.StatusBufferSize];
        // Bounded so a DLL that always reports "one more line" cannot spin here
        // forever and starve the poll.
        for (var i = 0; i < 32; i++)
        {
            if (!WxKeyInterop.GetStatusMessage(buffer, buffer.Length, out var level))
                return;

            var message = WxKeyInterop.DecodeBuffer(buffer);
            if (message.Length == 0)
                continue;

            if (level <= 1)
                log.Information("[wx_key] {Message}", message);
            else
                log.Warning("[wx_key] {Message}", message);
        }
    }

    private static bool WaitForExit(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!TryFindWeChatPid(out _))
                return true;

            Thread.Sleep(500);
        }

        return !TryFindWeChatPid(out _);
    }

    private static uint? WaitForStart(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (TryFindWeChatPid(out var pid))
                return pid;

            Thread.Sleep(500);
        }

        return TryFindWeChatPid(out var last) ? last : null;
    }

    /// <summary>
    /// Finds a running WeChat, covering both names it ships under. Handles are
    /// disposed - <see cref="Process.GetProcessesByName(string)"/> never
    /// releases them otherwise, and this is called in a tight polling loop.
    /// </summary>
    private static bool TryFindWeChatPid(out uint pid)
    {
        pid = 0;
        List<Process> processes = new();

        try
        {
            foreach (var name in WeChatProcessNames)
            {
                try
                {
                    processes.AddRange(Process.GetProcessesByName(name));
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Could not enumerate processes named {Name}", name);
                }
            }

            foreach (var process in processes)
            {
                // Reading Id can throw if the process exited between enumeration
                // and here, which in this loop is routine rather than exceptional.
                try
                {
                    pid = (uint)process.Id;
                    return true;
                }
                catch (InvalidOperationException)
                {
                    continue;
                }
            }

            return false;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length];
}
