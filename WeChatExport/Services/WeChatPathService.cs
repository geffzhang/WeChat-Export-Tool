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

    /// <summary>
    /// Subfolders below a data root (or below an account folder) that hold the
    /// message databases.
    /// </summary>
    /// <remarks>
    /// The WeChat 4.x layout is
    /// <c>&lt;root&gt;\xwechat_files\&lt;wxid&gt;_&lt;4 hex&gt;\db_storage\message\message_0.db</c>
    /// (research/experiments/m112/WECHAT_EXPORT_RESEARCH_HANDOVER.md:6, and the
    /// reference exporter's expected tree in
    /// research/reports/更新日志-第二次迭代.md:47), so <c>db_storage</c> alone is not
    /// enough - the database lives one level deeper, in <c>db_storage\message</c>.
    /// </remarks>
    private static readonly string[] MsgSubFolders = new[]
    {
        "Msg",                              // WeChat 3.x account folder
        "db_storage",                       // WeChat 4.x account folder
        Path.Combine("db_storage", "message"), // WeChat 4.x, where message_*.db lives
        "message"
    };

    /// <summary>
    /// Folders that hold one subfolder per WeChat account. A user-supplied root may
    /// be the parent of these (the research's root is <c>D:\储存信息</c>, whose child
    /// is <c>xwechat_files</c>), so the account level below them is searched too.
    /// </summary>
    private static readonly string[] AccountParentFolders = new[] { "xwechat_files", "WeChat Files" };

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
        // parent of one; try the root, its known subfolders, then one level of
        // account subfolders beneath it. When that level is itself an account-parent
        // folder (xwechat_files / WeChat Files), the account folders below it are
        // searched too - that is the WeChat 4.x shape named in the brief:
        // <root>\xwechat_files\<wxid>_<4 hex>\db_storage\message\message_0.db.
        var searchDirs = new List<string>();
        AddLayoutDirs(searchDirs, root);

        foreach (var child in SafeGetDirectories(root))
        {
            AddLayoutDirs(searchDirs, child);

            if (IsAccountParentFolder(child))
            {
                foreach (var accountDir in SafeGetDirectories(child))
                    AddLayoutDirs(searchDirs, accountDir);
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matches = new List<string>();
        foreach (var dir in searchDirs)
        {
            if (!seen.Add(dir) || !Directory.Exists(dir))
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

        // Unchanged rule: the most recently written database wins. Which of a
        // multi-shard message_0.db .. message_N.db install is authoritative is not
        // documented in the research, so no preference is invented here.
        return matches.OrderByDescending(File.GetLastWriteTime).First();
    }

    /// <summary>A folder and the known subfolders of an account it may contain.</summary>
    private static void AddLayoutDirs(List<string> searchDirs, string dir)
    {
        searchDirs.Add(dir);

        foreach (var sub in MsgSubFolders)
            searchDirs.Add(Path.Combine(dir, sub));
    }

    private static bool IsAccountParentFolder(string dir)
    {
        var name = Path.GetFileName(dir);
        return AccountParentFolders.Any(
            known => string.Equals(name, known, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> SafeGetDirectories(string dir)
    {
        try
        {
            return Directory.GetDirectories(dir);
        }
        catch (UnauthorizedAccessException)
        {
            // Not readable - nothing to search beneath it.
            return Array.Empty<string>();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
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
