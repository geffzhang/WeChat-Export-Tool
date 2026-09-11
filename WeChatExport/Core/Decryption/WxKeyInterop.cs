using System;
using System.Runtime.InteropServices;

namespace WeChatExport.Core.Decryption;

/// <summary>
/// P/Invoke surface of the native <c>wx_key.dll</c>, which injects a hook into
/// WeChat's process and reports the SQLCipher key WeChat hands to its database
/// layer at startup.
///
/// This DLL is the part of the original Python tool that could not be
/// reimplemented in managed code: the hook itself. The Node.js script that used
/// to drive it (<c>scripts/get_key.js</c>) was only a loader and orchestrator,
/// so replacing that script with <see cref="Services.ElevatedKeyCapture"/> drops
/// the Node/Electron sidecar entirely while keeping the proven native hook.
///
/// The exported names and signatures below were read out of the DLL's own
/// export table (x64, 6 named exports) and cross-checked against the two
/// independent call sites in the original tool: <c>scripts/get_key.js</c>
/// (koffi) and <c>research/scripts/m112_routeA/use_wx_key.py</c> (ctypes).
///
/// <para>
/// <b>Calling convention.</b> The DLL is x64-only, where Windows defines a
/// single calling convention; <see cref="CallingConvention.Cdecl"/> is therefore
/// nominal here. It would matter only for an x86 build, which this DLL is not.
/// </para>
/// <para>
/// <b>Bool marshalling.</b> Every predicate is declared to return a 1-byte
/// value (<see cref="UnmanagedType.I1"/>). If the DLL were instead built
/// returning a 4-byte <c>BOOL</c>, reading only the low byte still yields the
/// correct 0/1; the reverse choice would read undefined upper bits as a
/// non-zero "true". This is the safe direction to be wrong in.
/// </para>
/// </summary>
internal static class WxKeyInterop
{
    private const string DllName = "wx_key.dll";

    /// <summary>Size of the buffers the DLL fills, matching the original callers.</summary>
    public const int KeyBufferSize = 128;
    public const int ImageKeyBufferSize = 8192;
    public const int StatusBufferSize = 512;

    /// <summary>
    /// Injects the hook into the given process. WeChat 4.x only calls
    /// <c>SetDBKey</c> once, at process start, so this must run against a
    /// freshly started WeChat - before sign-in - or no key is ever observed.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool InitializeHook(uint targetPid);

    /// <summary>
    /// Non-blocking poll for the captured key. Writes an ASCII hex string into
    /// <paramref name="buffer"/> and returns false until the key is available.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool PollKeyData(byte[] buffer, int size);

    /// <summary>
    /// Drains one pending progress line into <paramref name="buffer"/>, with a
    /// severity in <paramref name="level"/>. Returns false when no line is
    /// pending. Call in a loop until it returns false.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetStatusMessage(byte[] buffer, int size, out int level);

    /// <summary>
    /// Extracts WeChat's image key as a UTF-8 JSON document. Only meaningful
    /// after a successful <see cref="PollKeyData"/>.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetImageKey(byte[] buffer, int size);

    /// <summary>Removes the hook. Safe to call whether or not one was installed.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CleanupHook();

    /// <summary>
    /// Last error message as a UTF-8 C string owned by the DLL, or a null
    /// pointer when there is none. Never freed by the caller.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GetLastErrorMsg();

    /// <summary>
    /// The DLL's last error message, decoded, or null when it reports none.
    /// </summary>
    public static string? LastError()
    {
        var ptr = GetLastErrorMsg();
        if (ptr == IntPtr.Zero)
            return null;

        var message = Marshal.PtrToStringUTF8(ptr);
        return string.IsNullOrWhiteSpace(message) ? null : message.Trim();
    }

    /// <summary>
    /// Decodes a NUL-terminated UTF-8 payload the DLL wrote into
    /// <paramref name="buffer"/>. The DLL logs Chinese status text, so this must
    /// not be treated as ASCII.
    /// </summary>
    public static string DecodeBuffer(byte[] buffer)
    {
        var end = Array.IndexOf(buffer, (byte)0);
        var length = end >= 0 ? end : buffer.Length;
        return System.Text.Encoding.UTF8.GetString(buffer, 0, length).Trim();
    }

    /// <summary>
    /// Whether <c>wx_key.dll</c> can actually be loaded, so the caller can tell
    /// "the DLL is missing" apart from "the capture failed". Probing this
    /// explicitly avoids reporting a bare <see cref="DllNotFoundException"/> as
    /// if it were a WeChat-side failure.
    /// </summary>
    public static bool IsAvailable()
    {
        try
        {
            // A call that touches the DLL without touching WeChat. CleanupHook is
            // safe with no hook installed, so it doubles as a load probe.
            CleanupHook();
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
