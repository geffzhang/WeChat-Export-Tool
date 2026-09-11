using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace WeChatExport.Services;

public class WeChatPathService
{
    /// <summary>Installation directories, used only to locate WeChat.exe / probe a registry install.</summary>
    private static readonly string[] InstallPaths = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tencent", "WeChat"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tencent", "WeChat"),
        @"C:\Program Files (x86)\Tencent\WeChat",
        @"C:\Program Files\Tencent\WeChat"
    };

    /// <summary>
    /// The single ordered list of plausible WeChat *data* roots. The older code only
    /// looked at %APPDATA%\Tencent\WeChat\Msg, which is not where WeChat 3.x/4.x
    /// actually stores chat history.
    /// </summary>
    private static readonly string[] DataRoots = new[]
    {
        // WeChat 4.x default location.
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "xwechat_files"),
        // WeChat 3.x default location; each account gets its own <wxid> subfolder.
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WeChat Files"),
        // Legacy roaming profile layouts.
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tencent", "WeChat"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tencent", "WeChat", "Msg")
    };

    /// <summary>Subfolders below a data root that hold the message databases.</summary>
    private static readonly string[] MsgSubFolders = new[] { "Msg", "db_storage" };

    /// <summary>
    /// File patterns for message databases: WeChat 3.x uses MSG*.db, WeChat 4.x uses
    /// message*.db. Glob matching on Windows is case-insensitive.
    /// </summary>
    private static readonly string[] MsgFilePatterns = new[] { "Msg*.db", "message*.db" };

    public string? DetectWeChatPath()
    {
        // Try registry first
        var registryPath = GetPathFromRegistry();
        if (!string.IsNullOrEmpty(registryPath) && IsValidWeChatPath(registryPath))
            return registryPath;

        // Scan common paths
        foreach (var path in InstallPaths)
        {
            if (IsValidWeChatPath(path))
                return path;
        }

        return null;
    }

    /// <summary>
    /// Returns the first known WeChat data root that exists on this machine, if any.
    /// </summary>
    public string? DetectWeChatDataRoot()
    {
        foreach (var root in DataRoots)
        {
            if (Directory.Exists(root))
                return root;
        }

        return null;
    }

    /// <summary>
    /// Finds the most recently written message database, searching the
    /// user-specified root first and then the known default locations.
    /// </summary>
    /// <param name="userSpecifiedRoot">
    /// Optional user-chosen data root (for example
    /// <c>Documents\WeChat Files\&lt;wxid&gt;</c>). Searched before the defaults.
    /// </param>
    public string? FindMsgDatabase(string? userSpecifiedRoot)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(userSpecifiedRoot))
            candidates.Add(userSpecifiedRoot.Trim());

        candidates.AddRange(DataRoots);

        foreach (var root in candidates)
        {
            var found = FindMsgDatabaseUnder(root);
            if (!string.IsNullOrEmpty(found))
                return found;
        }

        return null;
    }

    private static string? FindMsgDatabaseUnder(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return null;

        // The root may already be an account folder (…\WeChat Files\<wxid>) or the
        // parent of one; try the root, its known subfolders, and one level of
        // account subfolders beneath it.
        var searchDirs = new List<string> { root };

        foreach (var sub in MsgSubFolders)
            searchDirs.Add(Path.Combine(root, sub));

        try
        {
            foreach (var accountDir in Directory.GetDirectories(root))
            {
                searchDirs.Add(accountDir);
                foreach (var sub in MsgSubFolders)
                    searchDirs.Add(Path.Combine(accountDir, sub));
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Not readable - fall through with whatever we already collected.
        }
        catch (IOException)
        {
        }

        var matches = new List<string>();
        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (var pattern in MsgFilePatterns)
            {
                try
                {
                    matches.AddRange(Directory.GetFiles(dir, pattern));
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (IOException)
                {
                }
            }
        }

        if (matches.Count == 0)
            return null;

        return matches.OrderByDescending(File.GetLastWriteTime).First();
    }

    private string? GetPathFromRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Tencent\WeChat");
            return key?.GetValue("InstallPath") as string;
        }
        catch
        {
            return null;
        }
    }

    public bool IsValidWeChatPath(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return false;

        var wechatExe = Path.Combine(path, "WeChat.exe");
        return File.Exists(wechatExe);
    }
}
