using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Serilog;
using WeChatExport.Core.Decryption;
using WeChatExport.Core.Models;

namespace WeChatExport.Services;

/// <summary>
/// Distinguishes the ways a connect attempt can end, so the UI can tell the user
/// what actually went wrong instead of collapsing everything into "0 contacts".
/// </summary>
public enum ConnectOutcome
{
    /// <summary>
    /// Not a real outcome: the value an uninitialised <see cref="ConnectResult"/>
    /// carries. It exists so that <c>default(ConnectResult)</c> - whose Outcome is
    /// this zero member and whose Message is null - is a FAILURE rather than a
    /// success. With <see cref="Success"/> at zero, <c>default(ConnectResult).IsSuccess</c>
    /// was <c>true</c>.
    /// </summary>
    Unknown = 0,

    /// <summary>The database was opened, decrypted and contains the expected tables.</summary>
    Success,

    /// <summary>No path was supplied.</summary>
    InvalidPath,

    /// <summary>The path does not exist on disk.</summary>
    FileNotFound,

    /// <summary>The key was wrong (or the file is encrypted and the key was missing).</summary>
    KeyRejected,

    /// <summary>The file opened but is not a SQLite/SQLCipher database we can read.</summary>
    NotADatabase,

    /// <summary>
    /// Opened and decrypted, but the per-session <c>Msg_&lt;hash&gt;</c> tables this
    /// app reads are absent. The message lists the tables that <em>were</em> found
    /// so the mismatch is diagnosable instead of being a dead end.
    /// </summary>
    SchemaMismatch,

    /// <summary>Anything else (I/O error, provider error, ...).</summary>
    Failed
}

/// <summary>Outcome of a connect attempt plus a user-facing explanation.</summary>
public readonly record struct ConnectResult(ConnectOutcome Outcome, string Message)
{
    public bool IsSuccess => Outcome == ConnectOutcome.Success;
}

/// <summary>
/// Reads a WeChat 4.x message database.
/// </summary>
/// <remarks>
/// <para>
/// The schema this class reads is the one documented in this repository's research,
/// extracted from the live WeChat 4.x process:
/// </para>
/// <list type="bullet">
/// <item>research/experiments/m88/SCHEMA_SUMMARY.md - one <c>Msg_&lt;32-hex&gt;</c>
/// table per conversation.</item>
/// <item>research/experiments/m89/all_schema.sql:822 - the full
/// <c>SessionTable</c> CREATE TABLE.</item>
/// <item>research/experiments/m90/SENDER_MAPPING.md:6-14 - the
/// <c>Msg_&lt;hash&gt;.real_sender_id == SessionTable.rowid</c> mapping.</item>
/// <item>research/experiments/m112/WECHAT_EXPORT_RESEARCH_HANDOVER.md:42-90 and
/// WECHAT_EXPORT_FULL_CONTEXT.md:38-62 - the same schema and the
/// <c>username --MD5--&gt; Msg_&lt;hash&gt;</c> derivation.</item>
/// </list>
/// <para>
/// The previous implementation of this class read a table called <c>ChatInfo</c>
/// with <c>Sender</c>/<c>Receiver</c>/<c>Content</c> columns out of <c>MSG.db</c>.
/// That schema is invented: <c>grep -rn ChatInfo research/</c> returns 0 hits, so
/// against a real WeChat database it landed on <see cref="ConnectOutcome.SchemaMismatch"/>
/// and exported nothing. It has been removed rather than kept as a fallback.
/// </para>
/// <para>
/// NOTHING in the decryption path (PBKDF2 derivation, the single 4.x candidate,
/// <c>Pooling = false</c>, the SQLITE_NOTADB split) is affected by the schema work.
/// </para>
/// </remarks>
public class DatabaseService : IDisposable
{
    private const int SqliteNotADatabase = 26; // SQLITE_NOTADB

    /// <summary>
    /// SQLCipher keeps its per-database salt in the first 16 bytes of the file.
    /// It is per-file, NOT a constant - see ReadSalt().
    /// </summary>
    private const int SqlCipherSaltSize = 16;

    /// <summary>How many discovered table names to name in a schema-mismatch message.</summary>
    private const int DiscoveredTableListLimit = 15;

    /// <summary>
    /// WeChat 4.x stores one message table per conversation, named
    /// <c>Msg_&lt;32 hex characters&gt;</c>. The hex is the MD5 of the conversation's
    /// <c>SessionTable.username</c>.
    /// </summary>
    /// <remarks>
    /// UNVERIFIED: the derivation comes from the research
    /// (research/experiments/m90/SENDER_MAPPING.md:36 and
    /// research/experiments/m112/WECHAT_EXPORT_RESEARCH_HANDOVER.md:80-88, both
    /// "MD5(username) -&gt; Msg_&lt;hash&gt;"), not from a direct hash-and-compare
    /// against a live database. The research's own
    /// research/experiments/m90/sender_mapping.json records the hash as a method
    /// string and leaves "real_sender_id (INTEGER FK) table still not found".
    /// ResolveMessageTable() therefore treats the hash as a lookup key rather than
    /// as proof: it matches whatever <c>Msg_</c> table is actually present.
    /// </remarks>
    private const string MsgTablePrefix = "Msg_";

    /// <summary>The conversation-list table in a WeChat 4.x message_*.db.</summary>
    private const string SessionTableName = "SessionTable";

    /// <summary>
    /// Columns of a WeChat 4.x <c>Msg_&lt;hash&gt;</c> table
    /// (research/experiments/m88/SCHEMA_SUMMARY.md:20-38,
    /// research/experiments/m89/all_schema.sql:826). Only the columns this app maps
    /// are selected, so a build with extra or missing trailing columns still reads.
    /// </summary>
    private static readonly string[] MessageTableIdentityColumns =
    {
        "local_id", "message_content", "local_type", "create_time"
    };

    /// <summary>Optional <c>Msg_</c> columns, selected when the table has them.</summary>
    private static readonly string[] MessageTableOptionalColumns =
    {
        "real_sender_id", "server_id", "sort_seq", "source"
    };

    /// <summary>
    /// Columns of <c>SessionTable</c>. The brief lists nine; the research's own dump
    /// (research/experiments/m89/all_schema.sql:822) has nineteen. The research is
    /// primary evidence, so the extra columns are recognised here too - but only the
    /// ones that exist are selected (see ReadSessionRows), because a subset is all
    /// this app needs and an unknown-trimmed build must not break the read.
    /// </summary>
    private static readonly string[] SessionTableColumns =
    {
        "username", "unread_count", "summary", "last_timestamp", "sort_timestamp",
        "last_msg_sender", "last_sender_display_name", "type", "is_hidden"
    };

    /// <summary>
    /// One SQLCipher parameter set that WeChat has shipped. The key is derived with
    /// this candidate's KDF and handed to SQLCipher as an already-derived raw key.
    /// </summary>
    /// <remarks>
    /// There is deliberately no page-size field. The remaining candidate's 4096 is
    /// SQLCipher 4's own default, so it needs no PRAGMA. The old
    /// <c>PRAGMA cipher_page_size</c> branch could not have worked anyway: it was
    /// issued BEFORE <c>PRAGMA key</c>, and SQLCipher's handler is guarded by
    /// <c>if(ctx)</c> - before the key is set there is no codec context, so the
    /// statement silently returns success (SQLCipher 4.5.2 crypto.c:263-275). Per
    /// SQLCipher's documentation the pragma must be issued AFTER <c>PRAGMA key</c>
    /// and before the first database operation. A future legacy candidate should set
    /// it there, not before; it is not a broken pragma, only a wrongly ordered one.
    /// </remarks>
    private readonly record struct KeyCandidate(
        string Name,
        HashAlgorithmName Kdf,
        int Iterations);

    /// <summary>
    /// Ordered list of the SQLCipher parameter sets WeChat has shipped, tried until
    /// one opens the database. Connect() reports the one that won.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sources for the single entry below:
    /// <list type="bullet">
    /// <item>research/scripts/decrypt_direct.py:32 and research/scripts/decrypt.go:16,21,106
    /// - PBKDF2-HMAC-SHA512 x 256000 with a 4096-byte page, run against real V4 data.</item>
    /// <item>research/experiments/m112/decrypt_test.py:4 - "page=4096", and
    /// research/reports/更新日志-第二次迭代.md:16 /
    /// research/experiments/m112/WECHAT_EXPORT_RESEARCH_HANDOVER.md:376 -
    /// "SQLCipher 参数确认: PBKDF2-HMAC-SHA512 × 256000 次迭代".</item>
    /// </list>
    /// </para>
    /// <para>
    /// A WeChat 3.x / SQLCipher 3 candidate (PBKDF2-HMAC-SHA1 x 64000, page 1024) was
    /// REMOVED rather than corrected, and it is recorded here as a removed
    /// capability, not a working one. A real SQLCipher 3 database authenticates
    /// pages with HMAC-SHA1 over a 36-byte reserve, and it derives that HMAC key with
    /// PBKDF2-SHA1 (SQLCipher 3.4.2 crypto_impl.c:1235 uses
    /// <c>ctx-&gt;kdf_algorithm</c> for the HMAC key derivation too). The removed
    /// candidate set neither <c>cipher_hmac_algorithm</c> nor
    /// <c>cipher_kdf_algorithm</c>, so a genuine 3.x database could never have
    /// authenticated even with the correct key material. Removing the candidate means
    /// such a database is now reported as <see cref="ConnectOutcome.KeyRejected"/>
    /// with an explicit message, instead of being retried against a candidate that
    /// cannot succeed. Re-adding a real legacy path would need both pragmas, issued
    /// after <c>PRAGMA key</c> (see the page-size note above), plus 1024- and
    /// 4096-page variants - none of which has been verified against a real 3.x
    /// database or may be claimed as working.
    /// </para>
    /// </remarks>
    private static readonly KeyCandidate[] RawKeyCandidates =
    {
        new(
            "WeChat 4.x (PBKDF2-HMAC-SHA512, 256000 iterations, page size 4096)",
            HashAlgorithmName.SHA512,
            256000),
    };

    /// <summary>
    /// The account directory in a WeChat 4.x data root is named
    /// <c>&lt;wxid&gt;_&lt;4 hex&gt;</c>, e.g. <c>wxid_caccoealsdbj12_e8c8</c>
    /// (research/experiments/m112/WECHAT_EXPORT_RESEARCH_HANDOVER.md:6). The wxid is
    /// the leading part, so this pattern captures it and stops at the underscore
    /// before the suffix.
    /// </summary>
    private static readonly Regex OwnerWxidPattern =
        new(@"(?<wxid>wxid_[a-z0-9]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private SqliteConnection? _connection;
    private string? _databasePath;

    /// <summary>The user's 64-hex WeChat key, normalised to lowercase.</summary>
    private string? _rawHexKey;

    /// <summary>A non-hex key, applied as a SQLCipher passphrase.</summary>
    private string? _passphrase;

    /// <summary>
    /// How the current connection's key was actually applied, for logging and for
    /// the success message (e.g. which key candidate won).
    /// </summary>
    private string? _appliedKeyDescription;

    /// <summary>Real table names in the connected database.</summary>
    private List<string> _tableNames = new();

    /// <summary>
    /// The per-conversation <c>Msg_&lt;hash&gt;</c> tables actually present, in
    /// sqlite_master order. A conversation is one of these tables.
    /// </summary>
    private List<string> _messageTables = new();

    /// <summary>
    /// SessionTable rows with their rowids. <c>null</c> means "not read yet"; an
    /// empty list means the table is absent (the degraded case - see GetContacts).
    /// </summary>
    private List<SessionRow>? _sessionRows;

    private bool _sessionRowsLoaded;

    /// <summary>Conversation username -&gt; display name from the contact database.</summary>
    private Dictionary<string, string>? _contactNames;

    private bool _contactNamesLoaded;

    private bool _disposed;

    public bool IsConnected => _connection?.State == System.Data.ConnectionState.Open;

    public string? DatabasePath => _databasePath;

    /// <summary>
    /// Set when a query method fails, so callers can distinguish "there was
    /// genuinely nothing to return" from "the read blew up and we swallowed it".
    /// Cleared at the start of every query.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// The signed-in account's own wxid, taken from the account directory in the
    /// database path (e.g. <c>...\xwechat_files\wxid_caccoealsdbj12_e8c8\db_storage\...\</c>).
    /// Used to decide whether a message was written by the user (see
    /// <see cref="Message.IsFromSelf"/>). Null when the path carries no wxid, in
    /// which case no message is claimed to be from self.
    /// </summary>
    /// <remarks>
    /// UNVERIFIED as the *only* self-identification route: the brief nominates the
    /// account directory name as the signal, and no in-database flag for "written by
    /// me" exists in the documented schema. If this is null, self-authored messages
    /// keep their resolved sender username instead of rendering as "You" - the export
    /// still shows a stable identifier, never "Unknown".
    /// </remarks>
    private string? _ownerWxid;

    /// <summary>A row of SessionTable, with the rowid that message tables reference.</summary>
    private sealed record SessionRow(
        long RowId,
        string Username,
        long? UnreadCount,
        string? Summary,
        long? LastTimestamp,
        long? SortTimestamp,
        string? LastMessageSender,
        string? LastSenderDisplayName);

    /// <summary>
    /// Connects to a WeChat database file (<c>message_*.db</c> for WeChat 4.x, or
    /// <c>MSG*.db</c> for 3.x).
    /// </summary>
    /// <param name="dbPath">Path to the database file.</param>
    /// <param name="key">
    /// Optional decryption key. A 64-hex-character value is treated as WeChat's
    /// 32-byte key material, which is then run through the candidate KDFs (see
    /// <see cref="RawKeyCandidates"/>); anything else is treated as a passphrase.
    /// </param>
    public ConnectResult Connect(string dbPath, string? key = null)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            Log.Warning("Database path is null or empty");
            return new ConnectResult(ConnectOutcome.InvalidPath, "No database path was supplied.");
        }

        if (!File.Exists(dbPath))
        {
            Log.Warning("Database file not found: {DbPath}", dbPath);
            return new ConnectResult(ConnectOutcome.FileNotFound, $"Database file not found: {dbPath}");
        }

        Disconnect();

        // Declared outside the try: the catch clauses below need it to tell "wrong
        // key" apart from "not a database at all".
        var hasKey = !string.IsNullOrWhiteSpace(key);

        try
        {
            // WeChat message_*.db files are SQLCipher databases. WeChat hands SQLCipher
            // the 32-byte key material (normally printed as 64 hex characters) via
            // sqlite3_key(), and SQLCipher then runs its KDF over that material -
            // the file's own first 16 bytes are the salt. So the caller's 64-hex key
            // is NOT a SQLCipher raw key: it must be PBKDF2-derived first, and only
            // the DERIVED bytes may be passed in SQLCipher's raw-key syntax:
            //     PRAGMA key = "x'<hex of the derived 32 bytes>'"
            // Passing the underived 64 hex characters as a raw key (what this method
            // used to do) skips the KDF entirely and cannot open any real WeChat
            // database - the KDF result and the raw material are different keys.
            // Which PBKDF2 parameters WeChat used varies by version, so
            // OpenRawKeyConnection() tries RawKeyCandidates in order.
            //
            // Only non-hex input is a passphrase, via
            // SqliteConnectionStringBuilder.Password (which maps to sqlite3_key with
            // text semantics).
            _rawHexKey = null;
            _passphrase = null;
            if (hasKey)
            {
                if (CryptoUtils.TryNormalizeHexKey(key, out var normalizedHex))
                    _rawHexKey = normalizedHex;
                else
                    _passphrase = key;
            }

            _connection = OpenKeyedConnection(dbPath);

            // Opening proves nothing on its own: SQLite defers reading the header
            // until the first statement, so a wrong key or a non-database file only
            // fails here. Probe cheaply and report honestly.
            //
            // Depending on the mechanism, a bad key surfaces either at Open() (the
            // connection-string Password keyword) or at this first read (the raw-key
            // PRAGMA form is not), so both are covered by the SQLITE_NOTADB catch
            // clauses around this whole block.
            using (var probe = _connection.CreateCommand())
            {
                probe.CommandText = "SELECT count(*) FROM sqlite_master;";
                probe.ExecuteScalar();
            }

            // Discover what the database actually contains. WeChat 4.x puts one
            // message table per conversation, named Msg_<32 hex>, beside a
            // SessionTable conversation list; the schema is documented in the
            // research (see the class remarks). Discovery is by table-name prefix,
            // NOT by a hard-coded table: a real message_0.db has ~426 Msg_ tables
            // and no "ChatInfo" (research/experiments/m88/SCHEMA_SUMMARY.md:16).
            _tableNames = GetTableNames(_connection);
            _messageTables = FindMessageTables(_tableNames);

            if (_messageTables.Count == 0)
            {
                // A schema mismatch must still be reported as a failure - it must
                // never light a green "Connected" over an empty conversation list.
                // But it must also be diagnosable: with no real WeChat database
                // available while this app is being built, the table list is the
                // artifact needed to finish the real schema mapping, so name it
                // instead of stopping at "wrong version".
                Log.Warning(
                    "Opened {DbPath} but it contains no {Prefix}-prefixed message tables. Full table list ({TableCount}): {Tables}",
                    dbPath,
                    MsgTablePrefix,
                    _tableNames.Count,
                    string.Join(", ", _tableNames));
                DisposeConnection();

                return new ConnectResult(
                    ConnectOutcome.SchemaMismatch,
                    BuildSchemaMismatchMessage(_tableNames));
            }

            _ownerWxid = TryGetOwnerWxid(dbPath);

            _databasePath = dbPath;
            Log.Information(
                "Connected to WeChat database: {DbPath} (key applied: {HasKey}, {KeySource}; {MessageTableCount} message table(s); SessionTable present: {HasSessionTable}; owner wxid: {OwnerWxid})",
                dbPath,
                hasKey,
                _appliedKeyDescription,
                _messageTables.Count,
                HasSessionTable,
                _ownerWxid ?? "(unknown)");

            return new ConnectResult(ConnectOutcome.Success, "Connected successfully" + FormatKeySuffix() + DescribeSchema());
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteNotADatabase)
        {
            // SQLITE_NOTADB means the file could not be read as a database: either
            // the key is wrong, or the file is encrypted and no key was supplied.
            Log.Error(ex, "Database is not readable with the supplied key: {DbPath}", dbPath);
            DisposeConnection();

            return hasKey
                ? new ConnectResult(
                    ConnectOutcome.KeyRejected,
                    "The database could not be decrypted with the supplied key. Check the key, or clear it if the file is not encrypted. "
                  + "Note: this build knows only the WeChat 4.x parameter set (PBKDF2-HMAC-SHA512, 256000 iterations, page size 4096), "
                  + "so a database written by an older WeChat (SQLCipher 3.x) cannot be opened.")
                : new ConnectResult(
                    ConnectOutcome.NotADatabase,
                    "The file could not be read as a database. It looks encrypted - supply the decryption key.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to connect to database: {DbPath}", dbPath);
            DisposeConnection();
            return new ConnectResult(ConnectOutcome.Failed, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>True when the connected database has a SessionTable conversation list.</summary>
    private bool HasSessionTable =>
        _tableNames.Contains(SessionTableName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Names the key that actually opened the database, so a user can see (and
    /// report) which WeChat version's parameters worked rather than guessing.
    /// </summary>
    private string FormatKeySuffix()
    {
        return string.IsNullOrEmpty(_appliedKeyDescription)
            ? string.Empty
            : $" (key: {_appliedKeyDescription})";
    }

    /// <summary>
    /// One line describing the schema that was actually read, so the user can see
    /// whether the tool found a real WeChat 4.x layout or had to degrade.
    /// </summary>
    private string DescribeSchema()
    {
        var found = $" Found {_messageTables.Count} message table(s)";
        if (!HasSessionTable)
            found += " and no SessionTable: conversations are listed by their Msg_ table name and senders cannot be named";

        return found + ".";
    }

    /// <summary>
    /// Builds the schema-mismatch message: which tables were actually found (capped
    /// for readability - the full list is logged at Warning by the caller), and what
    /// this build does read.
    /// </summary>
    private static string BuildSchemaMismatchMessage(IReadOnlyList<string> foundTables)
    {
        var shown = foundTables.Count == 0
            ? "(none - the database contains no tables at all)"
            : string.Join(", ", foundTables.Take(DiscoveredTableListLimit));

        var truncated = foundTables.Count > DiscoveredTableListLimit
            ? $", ... ({foundTables.Count} tables in total)"
            : string.Empty;

        return $"The database opened and decrypted, but it contains no per-session message tables "
             + $"({MsgTablePrefix}<hash>) to read. Tables actually found: {shown}{truncated}. "
             + "This build reads WeChat 4.x message_*.db databases, which hold one Msg_<hash> table "
             + "per SessionTable row - please report the table list above.";
    }

    /// <summary>
    /// Lists the real table names in the database, so a schema mismatch can name
    /// what is there instead of only what is not.
    /// </summary>
    private static List<string> GetTableNames(SqliteConnection connection)
    {
        var tables = new List<string>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
                tables.Add(reader.GetString(0));
        }

        return tables;
    }

    /// <summary>
    /// The per-session message tables: every real table whose name starts with
    /// <c>Msg_</c>.
    /// </summary>
    /// <remarks>
    /// Full-text-search shadow tables are excluded by name: the documented FTS
    /// index for messages is <c>message_fts_v4_*</c>
    /// (research/experiments/m88/SCHEMA_SUMMARY.md:66) and its shadow tables
    /// (<c>_data</c>, <c>_idx</c>, <c>_content</c>, <c>_docsize</c>, <c>_config</c>)
    /// would otherwise be listed as conversations if a future build ever named them
    /// under the Msg_ prefix.
    /// </remarks>
    private static List<string> FindMessageTables(IReadOnlyList<string> tableNames)
    {
        return tableNames
            .Where(name =>
                name.StartsWith(MsgTablePrefix, StringComparison.OrdinalIgnoreCase)
                && !name.Contains("_fts", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Reads the signed-in account's wxid from the database path. WeChat stores each
    /// account under <c>&lt;root&gt;\xwechat_files\&lt;wxid&gt;_&lt;4 hex&gt;\db_storage\message\message_0.db</c>
    /// (research/experiments/m112/WECHAT_EXPORT_RESEARCH_HANDOVER.md:6).
    /// </summary>
    private static string? TryGetOwnerWxid(string dbPath)
    {
        var match = OwnerWxidPattern.Match(dbPath);
        return match.Success ? match.Groups["wxid"].Value : null;
    }

    /// <summary>
    /// Gets the conversations to show in the UI, one per SessionTable row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// R3: the conversation list comes from <c>SessionTable</c>, not from
    /// synthesising one entry per table name - <c>summary</c>, <c>last_timestamp</c>
    /// and <c>unread_count</c> are the preview, the sort time and the unread count.
    /// </para>
    /// <para>
    /// R9: when <c>SessionTable</c> is missing but <c>Msg_</c> tables exist, the
    /// conversations are still listed by table name and <see cref="LastError"/> says
    /// why they are unnamed, rather than failing outright.
    /// </para>
    /// <para>
    /// R6: a contact display name is used when one can be read from
    /// <c>contact.db</c> (WeChat 4.x, optional) or a sibling <c>MicroMsg.db</c>
    /// (3.x). With neither present the username itself is the display name - the
    /// list is never blank.
    /// </para>
    /// </remarks>
    public List<Contact> GetContacts()
    {
        LastError = null;
        var contacts = new List<Contact>();

        if (_connection == null || !IsConnected)
        {
            Log.Warning("Not connected to database");
            LastError = "Not connected to a database.";
            return contacts;
        }

        try
        {
            var names = LoadContactNames();
            var sessions = SessionRows;

            if (sessions is { Count: > 0 })
            {
                foreach (var session in sessions)
                    contacts.Add(ToContact(session, names));

                Log.Information(
                    "Retrieved {Count} conversation(s) from SessionTable", contacts.Count);
            }
            else
            {
                // Degraded: no conversation list. Name each Msg_ table so the user
                // still sees (and can export) every conversation that exists, and
                // say plainly that the names and unread counts are unavailable.
                foreach (var table in _messageTables)
                {
                    contacts.Add(new Contact
                    {
                        Identifier = table,
                        NickName = table,
                    });
                }

                // A read failure inside LoadSessionRows has already set LastError;
                // keep that (it names the real cause) and only describe the
                // degradation when the table was simply absent.
                LastError ??= contacts.Count == 0
                    ? "The database contains no conversations that could be listed."
                    : $"This database has no SessionTable, so {contacts.Count} conversation(s) are listed by their "
                    + "Msg_ table name instead of by username: names, previews and unread counts are unavailable.";

                Log.Warning(
                    "No SessionTable in {DbPath}; listed {Count} conversation(s) by message-table name",
                    _databasePath,
                    contacts.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read the conversation list");
            LastError = $"Failed to read the conversation list: {ex.Message}";
        }

        return contacts;
    }

    /// <summary>
    /// Projects a SessionTable row onto the existing <see cref="Contact"/> shape, so
    /// every export format keeps working unchanged.
    /// </summary>
    private static Contact ToContact(SessionRow session, IReadOnlyDictionary<string, string>? names)
    {
        string? name = null;
        names?.TryGetValue(session.Username, out name);

        return new Contact
        {
            // The identifier is the SessionTable username - the value the database
            // really keys a conversation by, and the value GetMessages resolves back
            // to a Msg_<hash> table.
            Identifier = session.Username,
            NickName = string.IsNullOrWhiteSpace(name) ? session.Username : name,
            UnreadCount = (int)Math.Clamp(session.UnreadCount ?? 0, 0, int.MaxValue),
            LastMessageTime = ToLocalTime(session.LastTimestamp),
            LastMessagePreview = string.IsNullOrWhiteSpace(session.Summary) ? null : session.Summary,
        };
    }

    /// <summary>
    /// The SessionTable conversation list, read once and cached. An empty list means
    /// the table is absent (or unreadable); the caller degrades.
    /// </summary>
    private List<SessionRow>? SessionRows
    {
        get
        {
            if (!_sessionRowsLoaded)
            {
                _sessionRowsLoaded = true;
                _sessionRows = LoadSessionRows();
            }

            return _sessionRows;
        }
    }

    /// <summary>
    /// Reads every SessionTable row together with its SQLite implicit rowid, which is
    /// what <c>Msg_&lt;hash&gt;.real_sender_id</c> references
    /// (research/experiments/m90/SENDER_MAPPING.md:6-14).
    /// </summary>
    /// <remarks>
    /// Only columns that actually exist are selected, so a build whose SessionTable
    /// is missing one of the optional columns still yields a conversation list.
    /// </remarks>
    private List<SessionRow>? LoadSessionRows()
    {
        if (_connection is null || !HasSessionTable)
            return null;

        try
        {
            var columns = GetColumnNames(_connection, SessionTableName);
            if (!columns.Contains("username", StringComparer.OrdinalIgnoreCase))
            {
                // Without username there is no conversation identity at all.
                Log.Warning(
                    "SessionTable in {DbPath} has no username column (columns: {Columns})",
                    _databasePath,
                    string.Join(", ", columns));
                return null;
            }

            var wanted = SessionTableColumns
                .Where(w => columns.Contains(w, StringComparer.OrdinalIgnoreCase))
                .ToList();
            var selectList = string.Join(", ", wanted.Select(QuoteIdentifier));

            var rows = new List<SessionRow>();
            using var command = _connection.CreateCommand();
            command.CommandText = $"SELECT rowid, {selectList} FROM {QuoteIdentifier(SessionTableName)};";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var rowId = reader.GetInt64(0);
                var username = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (string.IsNullOrWhiteSpace(username))
                    continue;

                // Ordinal 0 is the rowid injected above, so the projected columns
                // start at 1.
                rows.Add(new SessionRow(
                    rowId,
                    username.Trim(),
                    ReadLong(reader, wanted, 1, "unread_count"),
                    ReadString(reader, wanted, 1, "summary"),
                    ReadLong(reader, wanted, 1, "last_timestamp"),
                    ReadLong(reader, wanted, 1, "sort_timestamp"),
                    ReadString(reader, wanted, 1, "last_msg_sender"),
                    ReadString(reader, wanted, 1, "last_sender_display_name")));
            }

            // WeChat's own order: most recently active conversation first
            // (sort_timestamp is SessionTable's sort key; fall back to last_timestamp).
            rows.Sort((a, b) =>
            {
                var cmp = (b.SortTimestamp ?? b.LastTimestamp ?? 0)
                    .CompareTo(a.SortTimestamp ?? a.LastTimestamp ?? 0);
                return cmp != 0 ? cmp : b.RowId.CompareTo(a.RowId);
            });

            Log.Information(
                "SessionTable: {Count} row(s), mapped rowids {RowIds}",
                rows.Count,
                string.Join(", ", rows.Take(DiscoveredTableListLimit).Select(r => $"{r.RowId}={r.Username}")));

            return rows;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read SessionTable from {DbPath}", _databasePath);
            LastError = $"Failed to read the conversation list (SessionTable): {ex.Message}";
            return null;
        }
    }

    private static long? ReadLong(SqliteDataReader reader, IReadOnlyList<string> columns, int firstColumnOrdinal, string name)
    {
        var index = IndexOf(columns, name, firstColumnOrdinal);
        if (index < 0 || reader.IsDBNull(index))
            return null;

        return reader.GetInt64(index);
    }

    private static string? ReadString(SqliteDataReader reader, IReadOnlyList<string> columns, int firstColumnOrdinal, string name)
    {
        var index = IndexOf(columns, name, firstColumnOrdinal);
        if (index < 0 || reader.IsDBNull(index))
            return null;

        return reader.GetString(index);
    }

    private static int IndexOf(IReadOnlyList<string> columns, string name, int firstColumnOrdinal)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
                return i + firstColumnOrdinal;
        }

        return -1;
    }

    /// <summary>
    /// Gets the messages of one conversation, oldest first.
    /// </summary>
    /// <param name="contactIdentifier">
    /// A conversation identifier as the database stores it: a wxid such as
    /// <c>wxid_abc123</c> or a chatroom id such as <c>12345@chatroom</c> (a
    /// SessionTable username), or - in the degraded no-SessionTable case - the
    /// <c>Msg_&lt;hash&gt;</c> table name returned by <see cref="GetContacts"/>.
    /// </param>
    /// <param name="limit">Maximum number of messages to retrieve (default 1000).</param>
    /// <param name="conversationDisplayName">
    /// Display name of the conversation. Used as the sender name for the other party
    /// in a one-to-one chat, where it is the best available name.
    /// </param>
    /// <param name="senderNames">
    /// Optional per-sender lookup (sender identifier as stored in the database,
    /// e.g. a wxid, mapped to a contact display name). When a message's sender is
    /// present here it wins over everything else; this is what lets a group
    /// conversation attribute each message to the right person.
    /// </param>
    /// <returns>List of messages, oldest first.</returns>
    public List<Message> GetMessages(
        string contactIdentifier,
        int limit = 1000,
        string? conversationDisplayName = null,
        IReadOnlyDictionary<string, string>? senderNames = null)
    {
        LastError = null;
        var messages = new List<Message>();

        if (_connection == null || !IsConnected)
        {
            Log.Warning("Not connected to database");
            LastError = "Not connected to a database.";
            return messages;
        }

        // An empty identifier would filter on '' and quietly return nothing, which
        // is the same silent-empty-result failure the old numeric parameter caused.
        // Refuse it loudly instead.
        var identifier = contactIdentifier?.Trim();
        if (string.IsNullOrEmpty(identifier))
        {
            Log.Warning("GetMessages called without a conversation identifier");
            LastError = "No conversation identifier was supplied, so no conversation could be selected.";
            return messages;
        }

        try
        {
            var table = ResolveMessageTable(identifier);
            if (table is null)
            {
                Log.Warning(
                    "No message table for conversation {Conversation} in {DbPath} ({Count} message table(s))",
                    identifier,
                    _databasePath,
                    _messageTables.Count);
                LastError = $"No message table was found for conversation '{identifier}'. "
                          + (HasSessionTable
                                ? "Its username may have no Msg_<hash> table in this database."
                                : "This database has no SessionTable, so only the Msg_<hash> table names listed as conversations can be opened.");
                return messages;
            }

            var columns = GetColumnNames(_connection, table);
            var missing = MessageTableIdentityColumns
                .Where(required => !columns.Contains(required, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (missing.Count > 0)
            {
                LastError = $"The message table {table} is missing the column(s) this app reads "
                          + $"({string.Join(", ", missing)}). Columns actually found: {string.Join(", ", columns)}.";
                Log.Error("Message table {Table} is missing required columns: {Missing}", table, string.Join(", ", missing));
                return messages;
            }

            var wanted = MessageTableIdentityColumns
                .Concat(MessageTableOptionalColumns)
                .Where(c => columns.Contains(c, StringComparer.OrdinalIgnoreCase))
                .ToList();
            var selectList = string.Join(", ", wanted.Select(QuoteIdentifier));

            // Sender resolution: real_sender_id -> SessionTable.rowid -> username
            // (research/experiments/m90/SENDER_MAPPING.md:6-14). The map is null when
            // there is no SessionTable, in which case a sender keeps its raw id.
            var senderRowIds = SessionRows?.ToDictionary(r => r.RowId, r => r.Username);
            var names = LoadContactNames();

            using var cmd = new SqliteCommand(
                $"SELECT {selectList} FROM {QuoteIdentifier(table)} ORDER BY create_time DESC, local_id DESC LIMIT @Limit;",
                _connection);
            cmd.Parameters.AddWithValue("@Limit", limit);

            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                var localId = ReadLong(reader, wanted, 0, "local_id") ?? 0;
                var content = ReadString(reader, wanted, 0, "message_content");
                var localType = ReadLong(reader, wanted, 0, "local_type") ?? 0;
                var createTime = ReadLong(reader, wanted, 0, "create_time") ?? 0;
                var realSenderId = ReadLong(reader, wanted, 0, "real_sender_id");

                string? senderUsername = null;
                if (realSenderId is not null)
                    senderRowIds?.TryGetValue(realSenderId.Value, out senderUsername);

                var isFromSelf = senderUsername is not null
                    && _ownerWxid is not null
                    && string.Equals(senderUsername, _ownerWxid, StringComparison.OrdinalIgnoreCase);

                // The raw identifier is the fallback label, never "Unknown": an
                // unresolved real_sender_id is still a stable, distinguishable id.
                var senderIdentifier = senderUsername
                    ?? realSenderId?.ToString(CultureInfo.InvariantCulture);

                messages.Add(new Message
                {
                    MessageId = localId,
                    CreateTime = ToLocalTime(createTime) ?? DateTime.MinValue,
                    IsFromSelf = isFromSelf,
                    Content = content,

                    // The raw local_type is preserved as-is: the documented values map
                    // onto the existing enum (1=text, 3=image, 34=voice, 43=video,
                    // 47=emoji, 49=file/share - research/experiments/m88/SCHEMA_SUMMARY.md:25),
                    // and anything else keeps its numeric value rather than being
                    // dropped (an undefined enum member renders as its number).
                    Type = (MessageType)localType,

                    SenderId = isFromSelf ? 0 : realSenderId ?? 0,
                    SenderIdentifier = senderIdentifier,
                    // Only the other party needs a name: the UI and the exports render
                    // self-authored messages as "You".
                    SenderName = isFromSelf
                        ? null
                        : ResolveSenderName(senderIdentifier, identifier, conversationDisplayName, senderNames, names)
                });
            }

            // Reverse to get chronological order
            messages.Reverse();

            Log.Information(
                "Retrieved {Count} messages for conversation {Conversation} from {Table}",
                messages.Count,
                identifier,
                table);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to retrieve messages for conversation {Conversation}", identifier);
            LastError = $"Failed to read messages: {ex.Message}";
        }

        return messages;
    }

    /// <summary>
    /// Finds the <c>Msg_&lt;hash&gt;</c> table that holds a conversation's messages.
    /// </summary>
    /// <remarks>
    /// Accepts either the conversation username (a SessionTable username, hashed with
    /// MD5 - see <see cref="MsgTablePrefix"/>) or a table name already in the
    /// <c>Msg_</c> form (what the degraded no-SessionTable path hands out). The
    /// comparison is case-insensitive because the research shows lowercase hex but
    /// SQLite table names are compared case-insensitively anyway.
    /// </remarks>
    private string? ResolveMessageTable(string identifier)
    {
        // A conversation that was listed by its table name (degraded mode).
        var direct = _messageTables.FirstOrDefault(
            t => string.Equals(t, identifier, StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
            return direct;

        var hash = identifier.StartsWith(MsgTablePrefix, StringComparison.OrdinalIgnoreCase)
            ? identifier[MsgTablePrefix.Length..]
            : CryptoUtils.ComputeMd5String(identifier);

        return _messageTables.FirstOrDefault(t =>
            t.Length == MsgTablePrefix.Length + hash.Length
            && string.Equals(t[MsgTablePrefix.Length..], hash, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Picks the best available display name for a message's sender.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Preference order: an explicit per-sender mapping first (accurate for group
    /// chats), then the contact database's own name, then - only when the sender IS
    /// this conversation's own username - the conversation's display name. The last
    /// rule is deliberately narrow: with the real schema a group's senders are member
    /// wxids that are not the chatroom's username, so the old unconditional fallback
    /// would have labelled every group message with the chatroom's name.
    /// </para>
    /// <para>
    /// Returning null is a real outcome, not a failure: the exports then print
    /// <see cref="Message.SenderIdentifier"/> (a wxid or a raw real_sender_id) rather
    /// than "Unknown".
    /// </para>
    /// </remarks>
    private static string? ResolveSenderName(
        string? senderIdentifier,
        string? conversationIdentifier,
        string? conversationDisplayName,
        IReadOnlyDictionary<string, string>? senderNames,
        IReadOnlyDictionary<string, string>? contactNames)
    {
        if (string.IsNullOrEmpty(senderIdentifier))
            return null;

        if (senderNames is not null
            && senderNames.TryGetValue(senderIdentifier, out var mapped)
            && !string.IsNullOrWhiteSpace(mapped))
        {
            return mapped;
        }

        if (contactNames is not null
            && contactNames.TryGetValue(senderIdentifier, out var named)
            && !string.IsNullOrWhiteSpace(named))
        {
            return named;
        }

        if (string.Equals(senderIdentifier, conversationIdentifier, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(conversationDisplayName))
        {
            return conversationDisplayName;
        }

        return null;
    }

    /// <summary>
    /// Conversation/sender username -&gt; display name, read once and cached.
    /// </summary>
    /// <remarks>
    /// R6: every source here is OPTIONAL. A missing or unreadable contact database
    /// leaves the map empty, and the caller then falls back to the raw username, so
    /// the app is fully usable from <c>message_0.db</c> alone.
    /// </remarks>
    private Dictionary<string, string>? LoadContactNames()
    {
        if (_contactNamesLoaded)
            return _contactNames;

        _contactNamesLoaded = true;
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // WeChat 4.x: db_storage/contact/contact.db, table user_info
        // (research/experiments/m90/SENDER_MAPPING.md:43). UNVERIFIED: the research
        // names the table but not its columns, so the columns are discovered at
        // runtime from a candidate list rather than hard-coded (see ReadUserInfo).
        var contactDbPath = FindContactDatabasePath();
        if (contactDbPath is not null)
        {
            try
            {
                using var connection = OpenKeyedConnection(contactDbPath);
                ReadUserInfo(connection, names);
                Log.Information("Read {Count} contact name(s) from {ContactDb}", names.Count, contactDbPath);
            }
            catch (Exception ex)
            {
                // Optional by design (R6): never let this fail the connect.
                Log.Warning(ex, "Could not read contact names from {ContactDb}", contactDbPath);
            }
        }

        // WeChat 3.x kept contacts in a sibling MicroMsg.db.
        var microMsgPath = GetMicroMsgDatabasePath();
        if (microMsgPath is not null)
        {
            try
            {
                using var connection = OpenKeyedConnection(microMsgPath);
                ReadMicroMsgContacts(connection, names);
                Log.Information("Read {Count} contact name(s) in total after {MicroMsgDb}", names.Count, microMsgPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not read contact names from {MicroMsgDb}", microMsgPath);
            }
        }

        _contactNames = names;
        return names;
    }

    /// <summary>
    /// Finds the WeChat 4.x contact database for the connected message database:
    /// <c>db_storage/contact/contact.db</c> beside <c>db_storage/message/message_0.db</c>.
    /// </summary>
    /// <remarks>
    /// UNVERIFIED: the layout comes from the reference exporter's documented
    /// expectations (research/reports/更新日志-第二次迭代.md:47), not from a
    /// CREATE TABLE dump of a real contact.db. Returning null is a supported
    /// outcome - see R6.
    /// </remarks>
    private string? FindContactDatabasePath()
    {
        if (string.IsNullOrEmpty(_databasePath))
            return null;

        var messageDir = Path.GetDirectoryName(_databasePath);
        if (string.IsNullOrEmpty(messageDir))
            return null;

        var candidates = new List<string> { Path.Combine(messageDir, "contact.db") };

        // db_storage/message/message_0.db -> db_storage/{contact/contact.db,contact.db}
        var dbStorageDir = Path.GetDirectoryName(messageDir);
        if (!string.IsNullOrEmpty(dbStorageDir))
        {
            var contactDir = Path.Combine(dbStorageDir, "contact");
            candidates.Add(Path.Combine(contactDir, "contact.db"));
            candidates.Add(Path.Combine(dbStorageDir, "contact.db"));

            if (Directory.Exists(contactDir))
            {
                try
                {
                    candidates.AddRange(Directory.GetFiles(contactDir, "contact*.db"));
                }
                catch (IOException)
                {
                }
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Reads <c>user_info</c> from a WeChat 4.x contact.db.
    /// </summary>
    /// <remarks>
    /// UNVERIFIED, and deliberately tolerant: the table name <c>user_info</c> is the
    /// research's (research/experiments/m90/SENDER_MAPPING.md:43) but no column list
    /// for it appears anywhere in this repository, so the identity and name columns
    /// are chosen from candidate names at runtime. If none matches, no names are read
    /// and the app falls back to raw usernames rather than inventing a column.
    /// </remarks>
    private static void ReadUserInfo(SqliteConnection connection, Dictionary<string, string> names)
    {
        const string table = "user_info";
        var columns = GetColumnNames(connection, table);
        if (columns.Count == 0)
            return;

        var identityColumn = FindColumn(columns, "username", "user_name", "user_name_str", "wxid", "alias_name");
        var nickColumn = FindColumn(columns, "nick_name", "nickname", "nick_name_str", "alias");
        var remarkColumn = FindColumn(columns, "remark", "remark_name", "con_remark");
        if (identityColumn is null || (nickColumn is null && remarkColumn is null))
            return;

        var selectList = string.Join(", ", new[] { identityColumn, nickColumn, remarkColumn }
            .Where(c => c is not null)
            .Select(c => QuoteIdentifier(c!)));

        using var cmd = new SqliteCommand($"SELECT {selectList} FROM {QuoteIdentifier(table)};", connection);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var identity = reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(identity))
                continue;

            var nick = reader.FieldCount > 1 && !reader.IsDBNull(1)
                ? Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)
                : null;
            var remark = reader.FieldCount > 2 && !reader.IsDBNull(2)
                ? Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture)
                : null;

            var display = string.IsNullOrWhiteSpace(remark) ? nick : remark;
            if (!string.IsNullOrWhiteSpace(display))
                names[identity.Trim()] = display.Trim();
        }
    }

    /// <summary>First column whose name matches one of <paramref name="candidates"/>.</summary>
    private static string? FindColumn(IReadOnlyList<string> columns, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var match = columns.FirstOrDefault(
                c => string.Equals(c, candidate, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return null;
    }

    private string? GetMicroMsgDatabasePath()
    {
        if (string.IsNullOrEmpty(_databasePath))
            return null;

        var msgDir = Path.GetDirectoryName(_databasePath);
        if (string.IsNullOrEmpty(msgDir))
            return null;

        // WeChat 3.x keeps MicroMsg.db beside the MSG*.db files (in the Msg folder).
        var sibling = Path.Combine(msgDir, "MicroMsg.db");
        if (File.Exists(sibling))
            return sibling;

        // Some layouts nest it one level up, in a per-account MicroMsg folder.
        var accountDir = Path.GetDirectoryName(msgDir);
        if (string.IsNullOrEmpty(accountDir))
            return null;

        var microMsgDir = Path.Combine(accountDir, "MicroMsg");
        if (!Directory.Exists(microMsgDir))
            return null;

        foreach (var userDir in Directory.GetDirectories(microMsgDir))
        {
            var msgDb = Path.Combine(userDir, "MicroMsg.db");
            if (File.Exists(msgDb))
                return msgDb;
        }

        return null;
    }

    /// <summary>
    /// Reads names out of a 3.x <c>MicroMsg.db</c> Contact table.
    /// </summary>
    /// <remarks>
    /// UNVERIFIED AGAINST A REAL DATABASE: the table names and especially the
    /// DisplayName/Avatar columns are assumptions carried over from the original
    /// import; a real MicroMsg.db Contact table is not known to expose them. Kept
    /// because it costs nothing when the file is absent (which it is for every 4.x
    /// install) and because dropping a source of names would be a regression.
    /// </remarks>
    private static void ReadMicroMsgContacts(SqliteConnection connection, Dictionary<string, string> names)
    {
        foreach (var table in new[] { "Contact", "Contact_V2" })
        {
            try
            {
                var query = $"SELECT UserID, NickName, Remark, DisplayName FROM {QuoteIdentifier(table)}";
                using var cmd = new SqliteCommand(query, connection);
                using var reader = cmd.ExecuteReader();

                var found = false;
                while (reader.Read())
                {
                    // UserID is read as text on purpose: in a real WeChat database it
                    // is a wxid. Casting it to a number turned it into 0, which is why
                    // per-sender lookups could never match (see Contact.Identifier).
                    var identifier = reader.IsDBNull(0)
                        ? null
                        : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);

                    if (string.IsNullOrWhiteSpace(identifier))
                        continue;

                    var nickName = reader.IsDBNull(1) ? null : reader.GetString(1);
                    var remark = reader.IsDBNull(2) ? null : reader.GetString(2);
                    var displayName = string.IsNullOrWhiteSpace(remark) ? nickName : remark;
                    if (string.IsNullOrWhiteSpace(displayName))
                        continue;

                    names[identifier.Trim()] = displayName.Trim();
                    found = true;
                }

                if (found)
                    return;
            }
            catch (SqliteException)
            {
                // Table doesn't exist or query failed, try next table
            }
        }
    }

    /// <summary>The real column names of a table, or an empty list when it has none.</summary>
    private static List<string> GetColumnNames(SqliteConnection connection, string table)
    {
        var columns = new List<string>();

        try
        {
            using var command = connection.CreateCommand();
            // PRAGMA does not accept a bound parameter for the table name; the name
            // always comes from sqlite_master (or a constant), never from user input,
            // and is quoted so a stray character cannot change the statement.
            command.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(1))
                    columns.Add(reader.GetString(1));
            }
        }
        catch (SqliteException ex)
        {
            Log.Debug(ex, "Could not read the column list of {Table}", table);
        }

        return columns;
    }

    /// <summary>Quotes an SQL identifier (a table or column name) for interpolation.</summary>
    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// Converts a Unix timestamp, tolerating values outside the range
    /// <see cref="DateTimeOffset.FromUnixTimeSeconds"/> accepts (a corrupt or
    /// non-timestamp integer must not abort the whole conversation read).
    /// </summary>
    private static DateTime? ToLocalTime(long? unixSeconds)
    {
        if (unixSeconds is null)
            return null;

        return ToLocalTime(unixSeconds.Value);
    }

    private static DateTime? ToLocalTime(long unixSeconds)
    {
        if (unixSeconds is < -62135596800 or > 253402300799)
            return null;

        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;
    }

    /// <summary>
    /// Opens <paramref name="dbPath"/> applying the key material captured by
    /// <see cref="Connect"/>.
    /// </summary>
    /// <remarks>
    /// This is the single place key handling lives. Both connection sites - the main
    /// message database and the optional contact databases - go through it, so the
    /// derived-raw-key path and the passphrase path cannot drift apart again (the
    /// passphrase used to be applied to the main connection string only, which left
    /// the contact database being read unkeyed).
    /// </remarks>
    private SqliteConnection OpenKeyedConnection(string dbPath)
    {
        if (_rawHexKey is not null)
            return OpenRawKeyConnection(dbPath, _rawHexKey);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        };

        if (_passphrase is not null)
        {
            // Non-hex input is a passphrase: the connection-string Password keyword
            // maps to sqlite3_key with text semantics.
            builder.Password = _passphrase;

            // A keyed connection must NOT be pooled. See OpenRawKeyConnection().
            builder.Pooling = false;
        }

        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();

            _appliedKeyDescription = _passphrase is null ? "none (unencrypted)" : "passphrase";

            using (var probe = connection.CreateCommand())
            {
                probe.CommandText = "SELECT count(*) FROM sqlite_master;";
                probe.ExecuteScalar();
            }

            return connection;
        }
        catch
        {
            // A wrong passphrase is the NORMAL outcome of this path, not an edge
            // case: Open() applies the key and the first read rejects it, so the
            // catch below this one runs on every failed attempt - and Connect() is
            // retried by the UI whenever the user edits the key. Without this
            // disposal each failed attempt leaked the connection: a native sqlite3
            // handle and the applied key material, since this connection is
            // deliberately unpooled (see the Pooling note in
            // OpenRawKeyConnection). OpenRawKeyConnection already disposes its
            // failed candidates; this keeps the two keyed-open paths in step.
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens an encrypted database the way WeChat keyed it: the 64-hex key is
    /// <em>key material</em>, so each candidate's PBKDF2 is run over it (salted with
    /// the file's own first 16 bytes) and only the DERIVED 32 bytes are handed to
    /// SQLCipher in its raw-key syntax.
    /// </summary>
    /// <exception cref="SqliteException">
    /// The last failure, when no candidate opens the database. The caller's
    /// SQLITE_NOTADB handling turns that into <see cref="ConnectOutcome.KeyRejected"/>.
    /// </exception>
    private SqliteConnection OpenRawKeyConnection(string dbPath, string rawHexKey)
    {
        var keyBytes = Convert.FromHexString(rawHexKey);
        var salt = ReadSalt(dbPath);
        SqliteException? lastFailure = null;

        foreach (var candidate in RawKeyCandidates)
        {
            // The salt is the file's own first 16 bytes - per-file, not a constant.
            // (CryptoUtils.GetWeChatDefaultSalt() is unrelated to SQLCipher: it
            // returns the literal string "wxsecdbkey" and must not be used here.)
            var derivedHex = Convert.ToHexString(
                Rfc2898DeriveBytes.Pbkdf2(keyBytes, salt, candidate.Iterations, candidate.Kdf, 32))
                .ToLowerInvariant();

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,

                // A keyed connection must NOT be pooled. The key is applied by a
                // PRAGMA, so it is not part of the connection string and therefore
                // not part of Microsoft.Data.Sqlite's pool key. A pooled connection
                // keeps whichever key it was first opened with, so a later Connect()
                // with a different (even wrong) key would silently reuse it and
                // appear to succeed.
                Pooling = false
            };

            var connection = new SqliteConnection(builder.ToString());
            try
            {
                connection.Open();

                // No PRAGMA cipher_page_size here: the candidate's 4096 is SQLCipher
                // 4's default (nothing to set), and the pragma is a no-op in this
                // build anyway - see the remarks on RawKeyCandidates.

                // The derived bytes are passed as a RAW key, so SQLCipher must not
                // derive again - that is the point: the KDF has already been run in
                // C# with the candidate's parameters.
                using (var keyCommand = connection.CreateCommand())
                {
                    keyCommand.CommandText = $"PRAGMA key = \"x'{derivedHex}'\";";
                    keyCommand.ExecuteNonQuery();
                }

                // Opening alone proves nothing: SQLite defers reading the header until
                // the first statement, so a wrong key only fails here. This is also
                // what decides whether the candidate was the right one.
                using (var probe = connection.CreateCommand())
                {
                    probe.CommandText = "SELECT count(*) FROM sqlite_master;";
                    probe.ExecuteScalar();
                }

                _appliedKeyDescription = candidate.Name;
                Log.Information(
                    "Opened {DbPath} with SQLCipher key candidate: {Candidate}",
                    dbPath,
                    candidate.Name);
                return connection;
            }
            catch (SqliteException ex)
            {
                connection.Dispose();
                lastFailure = ex;
                Log.Debug(
                    ex,
                    "SQLCipher key candidate did not open {DbPath}: {Candidate}",
                    dbPath,
                    candidate.Name);
            }
        }

        if (lastFailure is not null)
            throw lastFailure;

        throw new InvalidOperationException("No SQLCipher key candidates are configured.");
    }

    /// <summary>
    /// Reads the SQLCipher salt, which is the first 16 bytes of the database file.
    /// </summary>
    private static byte[] ReadSalt(string dbPath)
    {
        var salt = new byte[SqlCipherSaltSize];
        using var stream = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        if (stream.Length < SqlCipherSaltSize)
        {
            throw new InvalidDataException(
                $"The database file is {stream.Length} bytes, too small to contain a SQLCipher header ({SqlCipherSaltSize} bytes).");
        }

        stream.ReadExactly(salt);
        return salt;
    }

    /// <summary>
    /// Disconnects from the database
    /// </summary>
    public void Disconnect()
    {
        try
        {
            if (_connection != null)
            {
                if (_connection.State == System.Data.ConnectionState.Open)
                {
                    Log.Information("Disconnected from database: {DbPath}", _databasePath);
                }
                DisposeConnection();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error disconnecting from database");
        }
        finally
        {
            _databasePath = null;
            _rawHexKey = null;
            _passphrase = null;
            _appliedKeyDescription = null;

            // Everything discovered about the previous database must go with it, or a
            // later Connect() would read the old schema's tables.
            _tableNames = new List<string>();
            _messageTables = new List<string>();
            _sessionRows = null;
            _sessionRowsLoaded = false;
            _contactNames = null;
            _contactNamesLoaded = false;
            _ownerWxid = null;
        }
    }

    private void DisposeConnection()
    {
        _connection?.Dispose();
        _connection = null;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                Disconnect();
            }
            _disposed = true;
        }
    }
}
