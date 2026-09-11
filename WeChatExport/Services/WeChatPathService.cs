using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
    /// <remarks>
    /// This glob is deliberately broad - it also collects files that are NOT message
    /// databases (<c>message_fts.db</c>, <c>message_resource.db</c>) - because the
    /// decision about which of them is a message shard is made by name in
    /// <see cref="IsMessageDatabaseFile"/>, not by the glob.
    /// </remarks>
    private static readonly string[] MsgFilePatterns = new[] { "Msg*.db", "message*.db" };

    /// <summary>
    /// A real WeChat 4.x message shard: <c>message_0.db</c>, <c>message_1.db</c>, ...
    /// </summary>
    /// <remarks>
    /// MEASURED against the user's real install: <c>db_storage\message\</c> holds
    /// <c>message_0.db</c> (990 <c>Msg_</c> tables), <c>message_1.db</c> (313),
    /// <c>message_fts.db</c> (the full-text index), <c>message_resource.db</c>,
    /// <c>biz_message_0.db</c> and <c>media_0.db</c>. WeChat shards messages across
    /// <c>message_N.db</c> <em>by time</em>, so one conversation's history can live in
    /// several shards (see <see cref="FindMessageShards"/>).
    /// </remarks>
    private static readonly Regex MessageShardNamePattern =
        new(@"^message_(?<index>\d+)\.db$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// File-name fragments that match the <c>message*.db</c> glob but are not
    /// message databases. Excluded BY NAME - the previous "newest mtime wins" rule
    /// selected <c>message_fts.db</c> (a 179 MB full-text index with no
    /// <c>Msg_</c> tables) over the real <c>message_1.db</c>, so the first run of
    /// the app failed to connect at all.
    /// </summary>
    private static readonly string[] NonMessageFileFragments = new[]
    {
        "_fts",             // message_fts.db / contact_fts.db - the search index
        "biz_message",      // biz_message_0.db - official-account messages
        "media_",           // media_0.db - media metadata
        "message_resource", // message_resource.db - resource metadata
    };

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

        // R0: pick a real message shard by NAME, deterministically - never by mtime.
        // Real 4.x installs hold message_0.db .. message_N.db beside the FTS index
        // and the media/biz stores, all written within the same seconds, so mtime is
        // both unstable and wrong. The lowest-numbered shard is the primary; every
        // other shard is merged in by DatabaseService (R6).
        return SelectMessageDatabases(matches).FirstOrDefault();
    }

    /// <summary>
    /// Every message database that belongs to the same install as
    /// <paramref name="messageDbPath"/>, the given file first.
    /// </summary>
    /// <remarks>
    /// R6: WeChat 4.x shards by time, so a conversation's messages may be split
    /// across <c>message_0.db</c> and <c>message_1.db</c>. MEASURED on the real
    /// install: <c>message_0.db</c> held 388 rows of one chat (2025-08-13 to
    /// 2026-08-26) and <c>message_1.db</c> the other 814 (2026-08-26 to
    /// 2026-09-11). Opening only one shard silently truncated every export.
    /// </remarks>
    public static IReadOnlyList<string> FindMessageShards(string messageDbPath)
    {
        var shards = new List<string>();

        if (!string.IsNullOrWhiteSpace(messageDbPath) && File.Exists(messageDbPath))
            shards.Add(messageDbPath);

        var dir = Path.GetDirectoryName(messageDbPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return shards;

        var siblings = new List<string>();
        foreach (var pattern in MsgFilePatterns)
        {
            try
            {
                siblings.AddRange(Directory.GetFiles(dir, pattern));
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }

        foreach (var sibling in SelectMessageDatabases(siblings))
        {
            if (!shards.Contains(sibling, StringComparer.OrdinalIgnoreCase))
                shards.Add(sibling);
        }

        return shards;
    }

    /// <summary>
    /// The WeChat 4.x session database (<c>db_storage\session\session.db</c>), found
    /// relative to a message database.
    /// </summary>
    /// <remarks>
    /// MEASURED: a real <c>SessionTable</c> (2,238 rows, holding <c>username</c>,
    /// <c>unread_count</c>, <c>summary</c>, <c>last_timestamp</c>, ...) lives in its
    /// own database, <c>db_storage\session\session.db</c>, NOT in the message
    /// database - <c>message_0.db</c> contains no <c>SessionTable</c> at all.
    /// Returning null is a supported outcome (the caller degrades honestly).
    /// </remarks>
    public static string? FindSessionDatabase(string messageDbPath)
    {
        if (string.IsNullOrWhiteSpace(messageDbPath))
            return null;

        var messageDir = Path.GetDirectoryName(messageDbPath);
        if (string.IsNullOrEmpty(messageDir))
            return null;

        var candidates = new List<string> { Path.Combine(messageDir, "session.db") };

        var dbStorageDir = Path.GetDirectoryName(messageDir);
        if (!string.IsNullOrEmpty(dbStorageDir))
        {
            var sessionDir = Path.Combine(dbStorageDir, "session");
            candidates.Add(Path.Combine(sessionDir, "session.db"));
            candidates.Add(Path.Combine(dbStorageDir, "session.db"));

            if (Directory.Exists(sessionDir))
            {
                try
                {
                    candidates.AddRange(Directory.GetFiles(sessionDir, "session*.db"));
                }
                catch (IOException)
                {
                }
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// The message databases among <paramref name="files"/>, filtered by name and
    /// ordered deterministically (real shards by index first, then legacy
    /// <c>Msg*.db</c>).
    /// </summary>
    private static IEnumerable<string> SelectMessageDatabases(IEnumerable<string> files)
    {
        return files
            .Where(IsMessageDatabaseFile)
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(MessageDatabaseRank)
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when a file name looks like a WeChat message database rather than one of
    /// the sibling stores that share the <c>message*.db</c> glob.
    /// </summary>
    private static bool IsMessageDatabaseFile(string file)
    {
        var name = Path.GetFileName(file);
        if (name.Length == 0)
            return false;

        if (NonMessageFileFragments.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            return false;

        // A transaction log is not a database.
        if (name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase))
            return false;

        return MessageShardNamePattern.IsMatch(name)
            || name.StartsWith("Msg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Order key: real shards by their index, legacy <c>Msg*.db</c> after them.</summary>
    private static int MessageDatabaseRank(string file)
    {
        var match = MessageShardNamePattern.Match(Path.GetFileName(file));
        return match.Success && int.TryParse(match.Groups["index"].Value, out var index)
            ? index
            : int.MaxValue;
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
