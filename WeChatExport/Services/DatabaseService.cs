using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Serilog;
using WeChatExport.Core.Decryption;
using WeChatExport.Core.Models;
using ZstdSharp;

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
/// Every schema fact this class relies on was MEASURED by opening the user's real
/// WeChat 4.x install (<c>%USERPROFILE%\Documents\xwechat_files\&lt;alias&gt;_&lt;4 hex&gt;\db_storage</c>)
/// with the captured key, and reading its real <c>sqlite_master</c> and rows. Where
/// this implementation disagrees with the repository's <c>research/</c> documents,
/// the real files win; the disagreement is called out at each site.
/// </para>
/// <list type="bullet">
/// <item><c>message_N.db</c> holds ~990 <c>Msg_&lt;md5(username)&gt;</c> tables and a
/// <c>Name2Id(rowid, user_name, is_session)</c> sender map. It has NO
/// <c>SessionTable</c>.</item>
/// <item><c>session\session.db</c> holds <c>SessionTable</c> - the conversation list,
/// with <c>summary</c>, <c>unread_count</c> and <c>last_timestamp</c>.</item>
/// <item><c>contact\contact.db</c> holds the name table <c>contact</c> (keyed by wxid
/// in <c>username</c>, names in <c>nick_name</c>/<c>remark</c>).</item>
/// <item><c>Msg_&lt;hash&gt;.real_sender_id</c> indexes <c>Name2Id.rowid</c> <em>of the
/// same shard</em> - the two shards' <c>Name2Id</c> tables assign different names to
/// the same rowid, so a sender MUST be resolved against the shard the row came
/// from.</item>
/// </list>
/// <para>
/// NOTHING in the decryption path (PBKDF2 derivation, the single 4.x candidate,
/// <c>Pooling = false</c>, the SQLITE_NOTADB split) is affected by this schema work.
/// The additional databases (<c>session.db</c>, <c>contact.db</c>, sibling shards)
/// are opened through the same <see cref="OpenKeyedConnection"/> with the same
/// unpooled discipline as the primary message database.
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
    /// MEASURED and confirmed: <c>md5("22442873678@chatroom")</c> equals the real
    /// table name <c>Msg_000a6ae0f8156e88410224c35c9b9aa8</c>, and 398/400 sampled
    /// <c>Msg_</c> tables in <c>message_0.db</c> (99.5%) hash to a live
    /// <c>SessionTable.username</c> - the 2 that do not are conversations whose
    /// session row has been deleted, not a broken derivation.
    /// </remarks>
    private const string MsgTablePrefix = "Msg_";

    /// <summary>The conversation-list table. In a real 4.x install it is in session.db.</summary>
    private const string SessionTableName = "SessionTable";

    /// <summary>
    /// The per-shard sender map: <c>Name2Id(rowid, user_name, is_session)</c>.
    /// <c>Msg_&lt;hash&gt;.real_sender_id</c> is a rowid into THIS table.
    /// </summary>
    /// <remarks>
    /// MEASURED and confirmed by self-validation: for 12 sampled group messages the
    /// <c>real_sender_id</c> looked up in <c>Name2Id</c> reproduced exactly the wxid
    /// that the message's own <c>message_content</c> carries as its
    /// <c>"&lt;wxid&gt;:\n"</c> prefix. It is emphatically NOT
    /// <c>SessionTable.rowid</c>: the two tables overlap for some ids, so the wrong
    /// table yields plausible-but-wrong names (rowid 112 is
    /// <c>wxid_9kwsy8ozmrtg22</c> in Name2Id and <c>43718220514@chatroom</c> in
    /// SessionTable).
    /// </remarks>
    private const string Name2IdTableName = "Name2Id";

    /// <summary>
    /// <c>WCDB_CT_message_content</c> value meaning "the content is zstd-compressed".
    /// </summary>
    /// <remarks>
    /// MEASURED: 963 of 4043 rows in one real <c>Msg_</c> table carry this flag, and
    /// those rows' <c>message_content</c> is a BLOB beginning <c>28 B5 2F FD</c> (the
    /// zstd magic). <c>compress_content</c> is empty in every one of them - the
    /// compressed bytes live in <c>message_content</c> itself, so the fallback column
    /// is read only if <c>message_content</c> turns out to be empty.
    /// </remarks>
    private const long CompressedContentFlag = 4;

    /// <summary>
    /// Marks content that was flagged as compressed but could not be decompressed.
    /// Emitting the raw bytes instead would put binary mojibake into all six export
    /// formats, which is worse than saying plainly that this message is unsupported.
    /// </summary>
    private const string UnsupportedContentMarker = "[unsupported compressed content]";

    /// <summary>How many 1:1 chats to scan when deriving the owner empirically.</summary>
    private const int OwnerScanChatLimit = 40;

    /// <summary>How many 1:1 chats must name a candidate before it is believed.</summary>
    private const int OwnerScanMinimumChats = 3;

    /// <summary>
    /// Columns of a WeChat 4.x <c>Msg_&lt;hash&gt;</c> table
    /// (MEASURED via <c>pragma_table_info</c>: local_id, server_id, local_type,
    /// sort_seq, real_sender_id, create_time, status, upload_status,
    /// download_status, server_seq, origin_source, source, message_content,
    /// compress_content, packed_info_data, WCDB_CT_message_content,
    /// WCDB_CT_source).
    /// </summary>
    private static readonly string[] MessageTableIdentityColumns =
    {
        "local_id", "message_content", "local_type", "create_time"
    };

    /// <summary>
    /// Optional <c>Msg_</c> columns, selected when the table has them.
    /// </summary>
    /// <remarks>
    /// <c>WCDB_CT_message_content</c> and <c>compress_content</c> are read so that
    /// zstd-compressed rows (R8) are decompressed instead of exported as raw binary.
    /// </remarks>
    private static readonly string[] MessageTableOptionalColumns =
    {
        "real_sender_id", "server_id", "sort_seq", "source",
        "WCDB_CT_message_content", "compress_content"
    };

    /// <summary>
    /// Columns of <c>SessionTable</c>. Only the ones that exist are selected (see
    /// ReadSessionRows), so a build whose SessionTable is missing an optional column
    /// still yields a conversation list.
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
    /// Confirmed against the user's real files: this candidate opens
    /// <c>message_0.db</c>, <c>message_1.db</c>, <c>session.db</c> and
    /// <c>contact.db</c>.
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
    /// The WeChat 4.x account folder is named <c>&lt;alias&gt;_&lt;4 hex&gt;</c> -
    /// MEASURED: this machine's is <c>geffzhang_6e17</c>, and <c>geffzhang</c> is a
    /// real identity in the databases (it is <c>Name2Id.user_name</c> with rowid 2 in
    /// both shards, a <c>SessionTable.username</c>, and
    /// <c>contact.username</c> with <c>nick_name</c> 张善友).
    /// </summary>
    /// <remarks>
    /// This REPLACES the old rule, which regexed a <c>wxid_...</c> token out of the
    /// database path and therefore never matched a real account folder (the alias
    /// contains no <c>wxid_</c>). The alias is only accepted after it is found in the
    /// databases - see <see cref="ResolveOwnerIdentity"/>.
    /// </remarks>
    private static readonly Regex OwnerAliasSuffixPattern =
        new(@"^(?<alias>.+)_[0-9a-f]{4}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The unread-count decoration WeChat puts in front of a busy session's summary,
    /// e.g. <c>"[52条] 广财生活圈: 恭喜！…"</c>. MEASURED: exactly 1 of the 2238 real
    /// summaries carries this form, because it is the only session with a merged
    /// multi-message summary.
    /// </summary>
    private static readonly Regex PreviewCountPrefixPattern =
        new(@"^\[\d+[条个]\s*\]\s*", RegexOptions.Compiled);

    /// <summary>
    /// A sender decoration in front of a summary. Deliberately narrow: the measured
    /// summaries include legitimate titles such as
    /// <c>"实测对比: 哪款开源 MySQL Kubernetes Operator 最值得用？"</c> and
    /// <c>"OpenAI证明: Navier–Stokes，真的会“炸”"</c> (and
    /// <c>"One more thing: iPhone Duo 横空出世"</c>, <c>"Google: “老于，你是第三名！”"</c>),
    /// so an arbitrary <c>"&lt;text&gt;: "</c> prefix must NOT be stripped. Only these
    /// forms are recognised as senders: a bracketed quoted sender
    /// (<c>"[军@114]:  OK，多谢"</c>), a custom-username prefix, a wxid, and a
    /// numeric chatroom/openim id.
    /// </summary>
    private static readonly Regex PreviewSenderPrefixPattern = new(
        @"^(?:"
        + @"\[[^\[\]:]{1,60}@\d+\]"
        + @"|_\$_CUSTOM_USERNAME_PREFIX_\$_[^:]{0,120}"
        + @"|wxid_[A-Za-z0-9_]{1,64}"
        + @"|\d{5,}@(?:chatroom|openim)"
        + @"|[A-Za-z0-9_.\-]{1,60}@(?:chatroom|openim)"
        + @"):\s+",
        RegexOptions.Compiled);

    /// <summary>
    /// The bare <c>"&lt;text&gt;: "</c> prefix that follows a merged-summary count
    /// decoration. Only applied there, where a sender is known to be present by
    /// construction.
    /// </summary>
    private static readonly Regex PreviewBarePrefixAfterCountPattern =
        new(@"^[^:]{1,60}:\s+", RegexOptions.Compiled);

    /// <summary>
    /// One opened message shard: the connection, the <c>Msg_</c> tables it holds, and
    /// its own <c>Name2Id</c> sender map (which differs from every other shard's).
    /// </summary>
    private sealed class Shard
    {
        public required string Path { get; init; }

        public required SqliteConnection Connection { get; init; }

        public required List<string> MessageTables { get; init; }

        private Dictionary<long, string>? _name2Id;
        private bool _name2IdLoaded;

        /// <summary>
        /// <c>Name2Id.rowid -&gt; user_name</c> for THIS shard, or null when the shard
        /// has no Name2Id table. Read once and cached.
        /// </summary>
        public Dictionary<long, string>? Name2Id
        {
            get
            {
                if (!_name2IdLoaded)
                {
                    _name2IdLoaded = true;
                    _name2Id = ReadName2Id(Connection);
                }

                return _name2Id;
            }
        }
    }

    /// <summary>A row of SessionTable, with the rowid that orders equally-timed rows.</summary>
    private sealed record SessionRow(
        long RowId,
        string Username,
        long? UnreadCount,
        string? Summary,
        long? LastTimestamp,
        long? SortTimestamp,
        string? LastMessageSender,
        string? LastSenderDisplayName);

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

    /// <summary>Real table names in the primary database.</summary>
    private List<string> _tableNames = new();

    /// <summary>
    /// Every opened message shard, primary first.
    /// </summary>
    private List<Shard> _shards = new();

    /// <summary>
    /// The per-conversation <c>Msg_&lt;hash&gt;</c> tables across all open shards,
    /// de-duplicated, in discovery order.
    /// </summary>
    private List<string> _messageTables = new();

    /// <summary>Table name -&gt; the shards that hold it.</summary>
    private Dictionary<string, List<Shard>> _messageTableShards =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SessionTable rows with their rowids. <c>null</c> means "not read yet"; an
    /// empty list means the table is absent (the degraded case - see GetContacts).
    /// </summary>
    private List<SessionRow>? _sessionRows;

    private bool _sessionRowsLoaded;

    /// <summary>The separate <c>session\session.db</c> when one was found and opened.</summary>
    private SqliteConnection? _sessionConnection;

    private string? _sessionDatabasePath;

    /// <summary>Conversation username -&gt; display name from the contact database.</summary>
    private Dictionary<string, string>? _contactNames;

    private bool _contactNamesLoaded;

    /// <summary>
    /// Identities that actually exist in the databases, used to accept or reject the
    /// account-folder alias as the owner. Built once from every shard's Name2Id plus
    /// contact.db.
    /// </summary>
    private HashSet<string>? _knownIdentities;

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
    /// True when the last <see cref="GetMessages"/> returned the newest
    /// <c>limit</c> messages but older ones exist in the database. The UI states this
    /// instead of reporting a silent, indistinguishable-from-complete result.
    /// </summary>
    public bool LastResultTruncated { get; private set; }

    /// <summary>
    /// The signed-in account's own identity, used to decide whether a message was
    /// written by the user (<see cref="Message.IsFromSelf"/>). Null when it could not
    /// be established, in which case no message is claimed to be from self.
    /// </summary>
    /// <remarks>
    /// MEASURED: on this install the owner's identity is <c>geffzhang</c> - the
    /// account folder is <c>geffzhang_6e17</c>, and <c>geffzhang</c> appears as
    /// <c>Name2Id.user_name</c> (rowid 2 in BOTH shards), as a
    /// <c>SessionTable.username</c>, and in <c>contact.db</c> as 张善友. In 87 of 120
    /// sampled 1:1 chats it is the only sender that is not the counterpart.
    /// </remarks>
    private string? _ownerIdentity;

    /// <summary>
    /// Connects to a WeChat database file (<c>message_*.db</c> for WeChat 4.x, or
    /// <c>MSG*.db</c> for 3.x) and, when they exist, to the sibling shards, the
    /// session database and the contact database.
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

        // Set BEFORE any discovery: FindContactDatabasePath, GetMicroMsgDatabasePath
        // and GetOwnerAliasCandidates all locate their files or the account folder
        // RELATIVE TO THIS PATH. Leaving it until the end of the method silently
        // disabled contact.db name resolution and the account-folder owner route -
        // every conversation showed a raw wxid and the owner was only ever found by
        // the empirical fallback. Set it here (the file's existence was checked
        // above) and let DisconnectInternal clear it on every failure path.
        _databasePath = dbPath;

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
            // NOT by a hard-coded table.
            _tableNames = GetTableNames(_connection);

            // Open every message shard that belongs to the same install (R6). A real
            // 4.x install splits one conversation's history by TIME across
            // message_0.db and message_1.db, so reading only one of them silently
            // truncated every export. A shard that will not open is skipped with a
            // warning rather than failing the whole connect.
            OpenShards(dbPath);

            _messageTables = _shards
                .SelectMany(s => s.MessageTables)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _messageTableShards = _shards
                .SelectMany(s => s.MessageTables.Select(t => (Table: t, Shard: s)))
                .GroupBy(x => x.Table, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => x.Shard).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            if (_messageTables.Count == 0)
            {
                // A schema mismatch must still be reported as a failure - it must
                // never light a green "Connected" over an empty conversation list.
                // But it must also be diagnosable: name the tables that WERE found
                // instead of stopping at "wrong version".
                Log.Warning(
                    "Opened {DbPath} but it (and its siblings) contain no {Prefix}-prefixed message tables. Full table list ({TableCount}): {Tables}",
                    dbPath,
                    MsgTablePrefix,
                    _tableNames.Count,
                    string.Join(", ", _tableNames));
                DisconnectInternal();
                DisposeConnections();

                return new ConnectResult(
                    ConnectOutcome.SchemaMismatch,
                    BuildSchemaMismatchMessage(_tableNames));
            }

            OpenSessionDatabase(dbPath);
            _ = SessionRows;              // read the conversation list once, up front
            _ = LoadContactNames();       // and the display names
            _ownerIdentity = ResolveOwnerIdentity();

            Log.Information(
                "Connected to WeChat database: {DbPath} (key applied: {HasKey}, {KeySource}; {ShardCount} shard(s) with {MessageTableCount} message table(s); session database: {SessionDb}; {SessionRowCount} session row(s); owner identity: {OwnerIdentity})",
                dbPath,
                hasKey,
                _appliedKeyDescription,
                _shards.Count,
                _messageTables.Count,
                _sessionDatabasePath ?? "(none)",
                SessionRows?.Count ?? 0,
                _ownerIdentity ?? "(unknown)");

            return new ConnectResult(ConnectOutcome.Success, "Connected successfully" + FormatKeySuffix() + DescribeSchema());
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteNotADatabase)
        {
            // SQLITE_NOTADB means the file could not be read as a database: either
            // the key is wrong, or the file is encrypted and no key was supplied.
            Log.Error(ex, "Database is not readable with the supplied key: {DbPath}", dbPath);
            DisposeConnections();
            DisconnectInternal(); // clear the path set above: this Connect did not succeed

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
            DisposeConnections();
            DisconnectInternal(); // clear the path set above: this Connect did not succeed
            return new ConnectResult(ConnectOutcome.Failed, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>True when a conversation list was found (in session.db, or in a message database).</summary>
    private bool HasSessionTable =>
        HasSessionDatabaseTable
        || _shards.Any(s => s.MessageTables.Count > 0
                            && GetTableNamesForShard(s).Contains(SessionTableName, StringComparer.OrdinalIgnoreCase));

    /// <summary>True when the separate session database was opened and has SessionTable.</summary>
    private bool HasSessionDatabaseTable =>
        _sessionConnection is not null && _sessionTableColumns is { Count: > 0 };

    /// <summary>The column names of the SessionTable that will actually be read.</summary>
    private List<string>? _sessionTableColumns;

    /// <summary>
    /// Opens the WeChat 4.x session database (<c>db_storage\session\session.db</c>)
    /// when it exists, and remembers its SessionTable columns.
    /// </summary>
    /// <remarks>
    /// MEASURED: this is where the real conversation list lives. Before this, the
    /// app looked only inside the message database, found no SessionTable, and
    /// listed 990 conversations as <c>Msg_&lt;32 hex&gt;</c> table names with no
    /// previews, no unread counts and no timestamps. A missing or unopenable
    /// session.db is a supported outcome: <see cref="GetContacts"/> then degrades to
    /// table names, as it did before.
    /// </remarks>
    private void OpenSessionDatabase(string dbPath)
    {
        var sessionDbPath = WeChatPathService.FindSessionDatabase(dbPath);
        if (sessionDbPath is null)
        {
            Log.Warning("No session database found beside {DbPath}; conversations will degrade to message-table names", dbPath);
            return;
        }

        try
        {
            var connection = OpenKeyedConnection(sessionDbPath);
            var columns = GetColumnNames(connection, SessionTableName);
            if (columns.Count == 0)
            {
                Log.Warning("{SessionDb} has no {Table} - ignoring it", sessionDbPath, SessionTableName);
                connection.Dispose();
                return;
            }

            _sessionConnection = connection;
            _sessionDatabasePath = sessionDbPath;
            _sessionTableColumns = columns;
            Log.Information("Opened session database {SessionDb} ({Table} columns: {Columns})",
                sessionDbPath, SessionTableName, string.Join(", ", columns));
        }
        catch (Exception ex)
        {
            // Optional by design: the message database alone is still usable.
            Log.Warning(ex, "Could not open the session database at {SessionDb}", sessionDbPath);
        }
    }

    /// <summary>The raw table list of one shard's connection (used for schema probing).</summary>
    private static List<string> GetTableNamesForShard(Shard shard) => GetTableNames(shard.Connection);

    /// <summary>
    /// Opens the primary database and every sibling message shard, in rank order,
    /// skipping any that will not open.
    /// </summary>
    private void OpenShards(string dbPath)
    {
        var primaryTables = FindMessageTables(_tableNames);

        _shards.Add(new Shard
        {
            Path = dbPath,
            Connection = _connection!,
            MessageTables = primaryTables,
        });

        if (primaryTables.Count == 0)
        {
            // The caller picked the FTS index (or another sibling store) instead of a
            // real shard - say so rather than reporting an empty schema.
            Log.Warning(
                "{DbPath} holds no {Prefix} tables; it looks like a search index or a secondary store, not a message database",
                dbPath,
                MsgTablePrefix);
        }

        foreach (var shardPath in WeChatPathService.FindMessageShards(dbPath))
        {
            if (string.Equals(shardPath, dbPath, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var connection = OpenKeyedConnection(shardPath);
                var tables = FindMessageTables(GetTableNames(connection));
                _shards.Add(new Shard
                {
                    Path = shardPath,
                    Connection = connection,
                    MessageTables = tables,
                });

                Log.Information("Opened message shard {ShardPath} ({TableCount} {Prefix} table(s))",
                    shardPath, tables.Count, MsgTablePrefix);
            }
            catch (Exception ex)
            {
                // R10: a shard that will not open is named and skipped, never
                // swallowed into a result that looks complete.
                Log.Warning(ex, "Could not open message shard {ShardPath}; its messages will be missing from exports", shardPath);
            }
        }

        if (primaryTables.Count == 0 && _shards.Count > 1)
        {
            var rescued = _shards.Skip(1).FirstOrDefault(s => s.MessageTables.Count > 0);
            if (rescued is not null)
            {
                Log.Warning(
                    "Substituting {Rescued} for {Given}: the given file holds no {Prefix} tables",
                    rescued.Path, dbPath, MsgTablePrefix);
            }
        }
    }

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
        var sessions = SessionRows?.Count ?? 0;
        var described = sessions > 0
            ? $" Found {_messageTables.Count} message table(s) in {_shards.Count} shard(s) and {sessions} conversation(s) in session.db."
            : $" Found {_messageTables.Count} message table(s) in {_shards.Count} shard(s): conversations are listed by their Msg_ table name and senders cannot be named.";

        if (_ownerIdentity is not null)
            described += $" Owner: {_ownerIdentity}.";

        return described;
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
    /// index for messages is <c>message_fts_v4_*</c> and its shadow tables
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
    /// Establishes the signed-in account's own identity (R7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two routes, both validated against real data:
    /// </para>
    /// <list type="number">
    /// <item>The account folder name. MEASURED: the folder is
    /// <c>&lt;alias&gt;_&lt;4 hex&gt;</c> (<c>geffzhang_6e17</c>) and the alias is the
    /// identity the databases actually use. The alias is accepted ONLY when it exists
    /// as a real identity in the databases (a <c>Name2Id.user_name</c>, a
    /// <c>SessionTable.username</c>, or a <c>contact.username</c>) - so a folder that
    /// is not an account folder, or an alias that was renamed, cannot produce a
    /// confident wrong answer.</item>
    /// <item>If that fails, the empirical derivation: in a 1:1 chat, the senders that
    /// are not the counterpart are the owner. MEASURED: scanning 120 real 1:1 chats
    /// produced exactly one candidate, <c>geffzhang</c>, in 87 of them.</item>
    /// </list>
    /// <para>
    /// If neither route is conclusive the owner stays null, and no message is claimed
    /// to be from self - the conservative outcome the brief asks for.
    /// </para>
    /// </remarks>
    private string? ResolveOwnerIdentity()
    {
        foreach (var candidate in GetOwnerAliasCandidates())
        {
            if (IsKnownIdentity(candidate))
            {
                Log.Information("Owner identity {Owner} taken from the account folder name and confirmed in the database", candidate);
                return candidate;
            }
        }

        var empirical = DeriveOwnerFromChats();
        if (empirical is not null)
        {
            Log.Information("Owner identity {Owner} derived empirically from 1:1 chats", empirical);
            return empirical;
        }

        Log.Warning("Could not establish the signed-in account's identity; no message will be marked as sent by the user");
        return null;
    }

    /// <summary>
    /// The plausible account identities from the database path's account folder, most
    /// specific first: the name with a trailing <c>_&lt;4 hex&gt;</c> removed, then the
    /// name as-is.
    /// </summary>
    /// <remarks>
    /// Both forms are offered because the suffix rule is ambiguous in the other
    /// direction: <c>wxid_abc123</c> (a 3.x account folder, or a wxid that happens to
    /// end in 4 hex characters) would otherwise be truncated to <c>wxid</c>. Each
    /// candidate is validated by <see cref="IsKnownIdentity"/>, so the wrong form is
    /// simply rejected.
    /// </remarks>
    private IEnumerable<string> GetOwnerAliasCandidates()
    {
        if (string.IsNullOrEmpty(_databasePath))
            yield break;

        // <db_storage>\message\message_0.db -> the account folder is two levels up.
        var messageDir = Path.GetDirectoryName(_databasePath);
        var accountDir = messageDir is null ? null : Path.GetDirectoryName(messageDir);
        var name = accountDir is null ? null : Path.GetFileName(accountDir);
        if (string.IsNullOrWhiteSpace(name))
            yield break;

        var match = OwnerAliasSuffixPattern.Match(name);
        if (match.Success && match.Groups["alias"].Value.Length > 0)
            yield return match.Groups["alias"].Value;

        yield return name;
    }

    /// <summary>
    /// Empirically derives the owner: the sender that keeps appearing in 1:1 chats
    /// without being the chat's counterpart.
    /// </summary>
    /// <remarks>
    /// Public because the real-database acceptance harness exercises it directly;
    /// nothing in the app calls it except <see cref="ResolveOwnerIdentity"/>.
    /// Bounded to <see cref="OwnerScanChatLimit"/> chats, and it only runs when the
    /// account-folder route was not conclusive, so it costs nothing on a normal
    /// install.
    /// </remarks>
    public string? DeriveOwnerFromChats()
    {
        var sessions = SessionRows;
        if (sessions is not { Count: > 0 })
            return null;

        var candidates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var probed = 0;

        foreach (var session in sessions)
        {
            if (probed >= OwnerScanChatLimit)
                break;

            // A 1:1 chat's counterpart is the username itself; in a group the
            // counterpart is a member, so groups are not evidence.
            var counterpart = session.Username;
            if (!counterpart.StartsWith("wxid_", StringComparison.OrdinalIgnoreCase))
                continue;

            var targets = ResolveMessageTableTargets(counterpart);
            if (targets.Count == 0)
                continue;

            foreach (var (shard, table) in targets)
            {
                foreach (var sender in ReadDistinctSenders(shard, table))
                {
                    if (string.Equals(sender, counterpart, StringComparison.OrdinalIgnoreCase))
                        continue;

                    candidates[sender] = candidates.GetValueOrDefault(sender) + 1;
                }
            }

            probed++;
        }

        if (candidates.Count == 0)
            return null;

        var ranked = candidates.OrderByDescending(kv => kv.Value).ToList();
        var best = ranked[0];
        var runnerUp = ranked.Count > 1 ? ranked[1].Value : 0;

        // Require the winner to be decisive, so a coincidence cannot become "self".
        if (best.Value < OwnerScanMinimumChats || best.Value < runnerUp * 2)
        {
            Log.Warning(
                "Owner derivation from 1:1 chats was inconclusive: {Best}={BestCount}, runner-up={RunnerUp}",
                best.Key, best.Value, runnerUp);
            return null;
        }

        return best.Key;
    }

    /// <summary>The distinct <c>Name2Id.user_name</c> values that authored rows in one table.</summary>
    private static IEnumerable<string> ReadDistinctSenders(Shard shard, string table)
    {
        var names = shard.Name2Id;
        if (names is null)
            yield break;

        var ids = new List<long>();
        using (var command = shard.Connection.CreateCommand())
        {
            command.CommandText = $"SELECT DISTINCT real_sender_id FROM {QuoteIdentifier(table)} LIMIT 200;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                    ids.Add(reader.GetInt64(0));
            }
        }

        foreach (var id in ids)
        {
            if (names.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name))
                yield return name;
        }
    }

    /// <summary>True when <paramref name="identity"/> exists in the opened databases.</summary>
    private bool IsKnownIdentity(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
            return false;

        _knownIdentities ??= BuildKnownIdentities();
        return _knownIdentities.Contains(identity);
    }

    private HashSet<string> BuildKnownIdentities()
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var shard in _shards)
        {
            var names = shard.Name2Id;
            if (names is null)
                continue;

            foreach (var name in names.Values)
                known.Add(name);
        }

        var contacts = LoadContactNames();
        foreach (var name in contacts.Keys)
            known.Add(name);

        return known;
    }

    /// <summary>
    /// Gets the conversations to show in the UI, one per SessionTable row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// R2: the conversation list is <c>SessionTable</c> from
    /// <c>session\session.db</c>, identified by <c>username</c> (a wxid or
    /// <c>&lt;id&gt;@chatroom</c>) - never by a <c>Msg_</c> table hash.
    /// </para>
    /// <para>
    /// R3: <c>summary</c>, <c>unread_count</c> and <c>last_timestamp</c> become the
    /// preview, the unread badge and the sort time.
    /// </para>
    /// <para>
    /// R1/R10: when no <c>SessionTable</c> can be read, the conversations degrade to
    /// the <c>Msg_</c> table names exactly as before, and <see cref="LastError"/>
    /// says why. A session whose messages are in no open shard is still listed (it is
    /// a real conversation) and opening it reports that plainly.
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

                // A Msg_ table with no surviving session row is still an exportable
                // conversation, so it is appended rather than hidden.
                var covered = sessions
                    .SelectMany(s => ResolveMessageTableTargets(s.Username).Select(t => t.Table))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var uncovered = _messageTables.Where(t => !covered.Contains(t)).ToList();
                foreach (var table in uncovered)
                {
                    contacts.Add(new Contact
                    {
                        Identifier = table,
                        NickName = table,
                    });
                }

                if (uncovered.Count > 0)
                {
                    Log.Information(
                        "Appended {Count} message table(s) with no session row: {Tables}",
                        uncovered.Count,
                        string.Join(", ", uncovered.Take(DiscoveredTableListLimit)));
                }

                Log.Information(
                    "Retrieved {Count} conversation(s) from {SessionDb}",
                    contacts.Count,
                    _sessionDatabasePath ?? "(message database SessionTable)");
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
                    : $"No SessionTable could be read ({_sessionDatabasePath ?? "no session database was found"}), "
                    + $"so {contacts.Count} conversation(s) are listed by their Msg_ table name instead of by username: "
                    + "names, previews and unread counts are unavailable.";

                Log.Warning(
                    "No SessionTable available for {DbPath}; listed {Count} conversation(s) by message-table name",
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
            LastMessageTime = ToLocalTime(session.LastTimestamp ?? session.SortTimestamp),
            LastMessagePreview = BuildPreview(session.Summary),
        };
    }

    /// <summary>
    /// Turns a raw SessionTable <c>summary</c> into a preview that reads as the last
    /// message (R3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// MEASURED over the 2063 non-empty real summaries: exactly 1 carries the
    /// unread-count form (<c>"[25条] 智东西: 15秒视频，54秒生成！…"</c>), exactly 1
    /// carries a bracketed quoted sender (<c>"[军@114]:  OK，多谢"</c>), and 9 contain
    /// <c>": "</c> at all - the other 7 being TITLES where the colon belongs to the
    /// text (<c>"实测对比: 哪款开源 MySQL Kubernetes Operator 最值得用？"</c>,
    /// <c>"OpenAI证明: Navier–Stokes，真的会“炸”"</c>, <c>"One more thing: iPhone Duo
    /// 横空出世"</c>, <c>"Google: “老于，你是第三名！”"</c>, <c>"收钱吧: 下单成功通知"</c>).
    /// So blanket <c>"&lt;sender&gt;: "</c> stripping - what the brief asks for - is
    /// unsafe on real data and is NOT done here. Two rules are used instead, both
    /// provable from the summary itself:
    /// </para>
    /// <list type="number">
    /// <item>The <c>[N条]</c> count decoration is always removed (it is unambiguously
    /// decoration). Because that form is a MERGED summary, whatever follows it up to
    /// the first colon IS a sender by construction, so that prefix is removed too.</item>
    /// <item>Elsewhere, a prefix is removed only when it is identifier-shaped (a
    /// bracketed quoted sender, a wxid, a chatroom/openim id, a custom-username
    /// prefix) - never a plain word or CJK string, which is what the title false
    /// positives look like.</item>
    /// </list>
    /// </remarks>
    private static string? BuildPreview(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return null;

        var text = summary.Trim();

        // "[25条] 智东西: 15秒视频…" -> the count is decoration, and the sender that
        // follows a merged-summary count is a sender by construction.
        var withoutCount = PreviewCountPrefixPattern.Replace(text, string.Empty, 1);
        if (withoutCount.Length != text.Length)
        {
            // The pattern can only match non-empty text, so a length change means the
            // count decoration was really there.
            text = PreviewBarePrefixAfterCountPattern.Replace(withoutCount, string.Empty, 1);
        }

        text = PreviewSenderPrefixPattern.Replace(text, string.Empty, 1).Trim();

        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// The SessionTable conversation list, read once and cached. An empty list means
    /// no SessionTable could be read; the caller degrades.
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
    /// Reads every SessionTable row together with its SQLite implicit rowid.
    /// </summary>
    /// <remarks>
    /// The table is read from <c>session\session.db</c> when that database was
    /// opened (the real 4.x layout), and from a message database that happens to
    /// carry one otherwise. Only columns that actually exist are selected.
    /// </remarks>
    private List<SessionRow>? LoadSessionRows()
    {
        var (connection, columns) = SessionSource();
        if (connection is null || columns is null)
            return null;

        try
        {
            if (!columns.Contains("username", StringComparer.OrdinalIgnoreCase))
            {
                // Without username there is no conversation identity at all.
                Log.Warning(
                    "SessionTable in {DbPath} has no username column (columns: {Columns})",
                    _sessionDatabasePath ?? _databasePath,
                    string.Join(", ", columns));
                return null;
            }

            var wanted = SessionTableColumns
                .Where(w => columns.Contains(w, StringComparer.OrdinalIgnoreCase))
                .ToList();
            var selectList = string.Join(", ", wanted.Select(QuoteIdentifier));

            var rows = new List<SessionRow>();
            using var command = connection.CreateCommand();
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
                "SessionTable: {Count} row(s) from {Source}",
                rows.Count,
                _sessionDatabasePath ?? _databasePath);

            return rows;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read SessionTable from {DbPath}", _sessionDatabasePath ?? _databasePath);
            LastError = $"Failed to read the conversation list ({SessionTableName}): {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Where SessionTable will be read from and what columns it has: the separate
    /// session database when available, otherwise a message shard that carries one.
    /// </summary>
    private (SqliteConnection? Connection, List<string>? Columns) SessionSource()
    {
        if (_sessionConnection is not null && _sessionTableColumns is { Count: > 0 })
            return (_sessionConnection, _sessionTableColumns);

        foreach (var shard in _shards)
        {
            if (!GetTableNamesForShard(shard).Contains(SessionTableName, StringComparer.OrdinalIgnoreCase))
                continue;

            var columns = GetColumnNames(shard.Connection, SessionTableName);
            if (columns.Count > 0)
                return (shard.Connection, columns);
        }

        return (null, null);
    }

    private static long? ReadLong(SqliteDataReader reader, IReadOnlyList<string> columns, int firstColumnOrdinal, string name)
    {
        var index = IndexOf(columns, firstColumnOrdinal, name);
        if (index < 0 || reader.IsDBNull(index))
            return null;

        return reader.GetInt64(index);
    }

    private static string? ReadString(SqliteDataReader reader, IReadOnlyList<string> columns, int firstColumnOrdinal, string name)
    {
        var index = IndexOf(columns, firstColumnOrdinal, name);
        if (index < 0 || reader.IsDBNull(index))
            return null;

        return reader.GetString(index);
    }

    private static int IndexOf(IReadOnlyList<string> columns, int firstColumnOrdinal, string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
                return i + firstColumnOrdinal;
        }

        return -1;
    }

    /// <summary>
    /// Gets the messages of one conversation, oldest first, merged across every
    /// message shard that holds part of it (R6).
    /// </summary>
    /// <param name="contactIdentifier">
    /// A conversation identifier as the database stores it: a wxid such as
    /// <c>wxid_abc123</c> or a chatroom id such as <c>12345@chatroom</c> (a
    /// SessionTable username), or - in the degraded no-SessionTable case - the
    /// <c>Msg_&lt;hash&gt;</c> table name returned by <see cref="GetContacts"/>.
    /// </param>
    /// <param name="limit">
    /// Maximum number of messages to retrieve (default 1000). WeChat 4.x history is
    /// far longer than this, so <see cref="LastResultTruncated"/> is set when older
    /// messages exist beyond the returned window.
    /// </param>
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
        LastResultTruncated = false;
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

        if (limit <= 0)
        {
            Log.Warning("GetMessages called with a non-positive limit ({Limit})", limit);
            LastError = $"The requested message limit ({limit}) is not a positive number.";
            return messages;
        }

        try
        {
            var targets = ResolveMessageTableTargets(identifier);
            if (targets.Count == 0)
            {
                Log.Warning(
                    "No message table for conversation {Conversation} in {DbPath} ({Count} message table(s))",
                    identifier,
                    _databasePath,
                    _messageTables.Count);
                LastError = $"No message table was found for conversation '{identifier}'. "
                          + (_messageTables.Any()
                                ? "Its username may have no Msg_<hash> table in any open shard, or the shard that holds it did not open."
                                : "No Msg_<hash> message tables were found at all.");
                return messages;
            }

            var names = LoadContactNames();
            var collected = new List<(long CreateTime, long LocalId, Message Message)>();
            var truncated = false;

            foreach (var (shard, table) in targets)
            {
                var read = ReadShardMessages(
                    shard, table, identifier, limit, conversationDisplayName, senderNames, names);

                if (read.Truncated)
                    truncated = true;

                collected.AddRange(read.Rows);
            }

            // R6: order by create_time (falling back to local_id) and de-duplicate,
            // then take the newest `limit`. MEASURED: the two shards' local_id
            // sequences are INDEPENDENT (local_id 1 in message_0.db is a different
            // message from local_id 1 in message_1.db), so local_id alone is not an
            // identity - but (create_time, local_id) had zero collisions across
            // 26,862 shared ids in 15 real conversations, and no shard repeats a
            // local_id internally.
            var seen = new HashSet<(long CreateTime, long LocalId)>();
            var merged = collected
                .OrderByDescending(r => r.CreateTime)
                .ThenByDescending(r => r.LocalId)
                .Where(r => seen.Add((r.CreateTime, r.LocalId)))
                .ToList();

            if (merged.Count > limit)
                truncated = true;

            var newest = merged.Take(limit).Select(r => r.Message).ToList();
            newest.Reverse();

            messages = newest;
            LastResultTruncated = truncated;

            Log.Information(
                "Retrieved {Count} messages for conversation {Conversation} from {ShardCount} shard(s){Truncated}",
                messages.Count,
                identifier,
                targets.Count,
                truncated ? " (older messages exist)" : string.Empty);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to retrieve messages for conversation {Conversation}", identifier);
            LastError = $"Failed to read messages: {ex.Message}";
        }

        return messages;
    }

    /// <summary>Reads one shard's contribution to a conversation.</summary>
    private (List<(long CreateTime, long LocalId, Message Message)> Rows, bool Truncated) ReadShardMessages(
        Shard shard,
        string table,
        string conversationIdentifier,
        int limit,
        string? conversationDisplayName,
        IReadOnlyDictionary<string, string>? senderNames,
        IReadOnlyDictionary<string, string>? contactNames)
    {
        var rows = new List<(long, long, Message)>();

        var columns = GetColumnNames(shard.Connection, table);
        var missing = MessageTableIdentityColumns
            .Where(required => !columns.Contains(required, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (missing.Count > 0)
        {
            LastError = $"The message table {table} is missing the column(s) this app reads "
                      + $"({string.Join(", ", missing)}). Columns actually found: {string.Join(", ", columns)}.";
            Log.Error("Message table {Table} is missing required columns: {Missing}", table, string.Join(", ", missing));
            return (rows, false);
        }

        var wanted = MessageTableIdentityColumns
            .Concat(MessageTableOptionalColumns)
            .Where(c => columns.Contains(c, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var selectList = string.Join(", ", wanted.Select(QuoteIdentifier));

        // Sender resolution: real_sender_id -> THIS SHARD's Name2Id.rowid -> user_name.
        // MEASURED: the shards' Name2Id tables disagree (rowid 480 is
        // wxid_e6s0alqrn9en22 in message_0.db and yixinweini_ in message_1.db), so
        // resolving through a single global map would name senders wrongly.
        var senderRowIds = shard.Name2Id;

        using var cmd = new SqliteCommand(
            $"SELECT {selectList} FROM {QuoteIdentifier(table)} ORDER BY create_time DESC, local_id DESC LIMIT @Limit;",
            shard.Connection);
        cmd.Parameters.AddWithValue("@Limit", limit);

        using var reader = cmd.ExecuteReader();

        var read = 0;
        while (reader.Read())
        {
            read++;

            var localId = ReadLong(reader, wanted, 0, "local_id") ?? 0;
            var localType = ReadLong(reader, wanted, 0, "local_type") ?? 0;
            var createTime = ReadLong(reader, wanted, 0, "create_time") ?? 0;
            var realSenderId = ReadLong(reader, wanted, 0, "real_sender_id");

            string? senderUsername = null;
            if (realSenderId is not null)
                senderRowIds?.TryGetValue(realSenderId.Value, out senderUsername);

            var isFromSelf = senderUsername is not null
                && _ownerIdentity is not null
                && string.Equals(senderUsername, _ownerIdentity, StringComparison.OrdinalIgnoreCase);

            // The raw identifier is the fallback label, never "Unknown" and never a
            // bare number: an unresolved real_sender_id is still a stable,
            // distinguishable, clearly-synthetic id.
            var senderIdentifier = senderUsername
                ?? (realSenderId is null
                    ? null
                    : $"sender#{realSenderId.Value.ToString(CultureInfo.InvariantCulture)}");

            // R8: a row whose WCDB_CT flag says "4" holds a zstd frame, NOT text.
            // Emitting those bytes raw put binary mojibake into all six formats.
            var content = ReadContent(reader, wanted, table, localId, out var contentUnsupported);

            rows.Add((createTime, localId, new Message
            {
                MessageId = localId,
                CreateTime = ToLocalTime(createTime) ?? DateTime.MinValue,
                IsFromSelf = isFromSelf,
                Content = content,

                // R9: the raw local_type is preserved in full (RawLocalType) and the
                // coarse type is its LOW 32 BITS. MEASURED: every real value's low 32
                // bits is one of 1, 3, 43, 47, 49, 10000 - the high bits carry the
                // appmsg SUBTYPE (5 = link, 57 = quote, 63 = channels), which the old
                // `(MessageType)longValue` truncation discarded. An unmapped value
                // still renders as its number rather than being forced into a wrong
                // label.
                Type = (MessageType)(int)(localType & 0xFFFFFFFFL),
                RawLocalType = localType,

                SenderId = isFromSelf ? 0 : realSenderId ?? 0,
                SenderIdentifier = senderIdentifier,
                // Only the other party needs a name: the UI and the exports render
                // self-authored messages as "You".
                SenderName = isFromSelf
                    ? null
                    : ResolveSenderName(senderIdentifier, conversationIdentifier, conversationDisplayName, senderNames, contactNames),
                ContentUnsupported = contentUnsupported,
            }));
        }

        return (rows, read >= limit);
    }

    /// <summary>
    /// Reads a row's content, decompressing it when the WCDB content-type flag says
    /// it is a zstd frame (R8).
    /// </summary>
    /// <remarks>
    /// MEASURED: for a flagged row, <c>message_content</c> is a BLOB starting
    /// <c>28 B5 2F FD</c> and <c>compress_content</c> is empty, and decompressing it
    /// yields the message text (group messages keep their <c>"&lt;wxid&gt;:\n"</c>
    /// prefix). Content that is flagged but cannot be decompressed is replaced by
    /// <see cref="UnsupportedContentMarker"/> rather than passed through as bytes.
    /// </remarks>
    private static string? ReadContent(
        SqliteDataReader reader,
        IReadOnlyList<string> columns,
        string table,
        long localId,
        out bool unsupported)
    {
        unsupported = false;

        var contentIndex = IndexOf(columns, 0, "message_content");

        var flagIndex = IndexOf(columns, 0, "WCDB_CT_message_content");
        var compressed = flagIndex >= 0
                         && !reader.IsDBNull(flagIndex)
                         && reader.GetInt64(flagIndex) == CompressedContentFlag;

        if (!compressed)
        {
            return contentIndex < 0 || reader.IsDBNull(contentIndex)
                ? null
                : reader.GetString(contentIndex);
        }

        var bytes = ReadBytes(reader, contentIndex)
                    ?? ReadBytes(reader, IndexOf(columns, 0, "compress_content"));

        if (bytes is null || bytes.Length == 0)
        {
            unsupported = true;
            return UnsupportedContentMarker;
        }

        try
        {
            using var decompressor = new Decompressor();
            var text = Encoding.UTF8.GetString(decompressor.Unwrap(bytes));

            if (text.Length > 0)
                return text;

            Log.Warning("zstd-compressed content in {Table} local_id={LocalId} decompressed to nothing", table, localId);
            unsupported = true;
            return UnsupportedContentMarker;
        }
        catch (Exception ex)
        {
            // Never fall back to the raw bytes: they are not text, and exporting
            // them would put mojibake into six formats.
            Log.Warning(ex, "Could not decompress the content of {Table} local_id={LocalId} ({Bytes} bytes)", table, localId, bytes.Length);
            unsupported = true;
            return UnsupportedContentMarker;
        }
    }

    /// <summary>A column's value as raw bytes, whether the driver hands back a BLOB or a string.</summary>
    private static byte[]? ReadBytes(SqliteDataReader reader, int index)
    {
        if (index < 0 || reader.IsDBNull(index))
            return null;

        return reader.GetValue(index) switch
        {
            byte[] blob => blob,
            string text => Encoding.UTF8.GetBytes(text),
            _ => null,
        };
    }

    /// <summary>
    /// Every <c>Msg_&lt;hash&gt;</c> table, in every open shard, that holds the given
    /// conversation.
    /// </summary>
    private List<(Shard Shard, string Table)> ResolveMessageTableTargets(string identifier)
    {
        var targets = new List<(Shard, string)>();

        // A conversation that was listed by its table name (degraded mode).
        if (_messageTableShards.TryGetValue(identifier, out var direct))
        {
            foreach (var shard in direct)
                targets.Add((shard, _messageTableShards.Keys.First(k => string.Equals(k, identifier, StringComparison.OrdinalIgnoreCase))));
            return targets;
        }

        var hash = identifier.StartsWith(MsgTablePrefix, StringComparison.OrdinalIgnoreCase)
            ? identifier[MsgTablePrefix.Length..]
            : CryptoUtils.ComputeMd5String(identifier);
        var tableName = MsgTablePrefix + hash;

        if (_messageTableShards.TryGetValue(tableName, out var holders))
        {
            foreach (var shard in holders)
                targets.Add((shard, tableName));
        }

        return targets;
    }

    /// <summary>
    /// Picks the best available display name for a message's sender.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Preference order: an explicit per-sender mapping first (accurate for group
    /// chats), then the contact database's own name, then - only when the sender IS
    /// this conversation's own username - the conversation's display name. The last
    /// rule is deliberately narrow: a group's senders are member wxids that are not
    /// the chatroom's username, so an unconditional fallback would label every group
    /// message with the chatroom's name.
    /// </para>
    /// <para>
    /// Returning null is a real outcome, not a failure: the exports then print
    /// <see cref="Message.SenderIdentifier"/> (a wxid) rather than "Unknown".
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
    /// R5: every source here is OPTIONAL. A missing or unreadable contact database
    /// leaves the map empty, and the caller then falls back to the raw wxid, so the
    /// app is fully usable from the message databases alone.
    /// </remarks>
    private Dictionary<string, string> LoadContactNames()
    {
        if (_contactNamesLoaded)
            return _contactNames!;

        _contactNamesLoaded = true;
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // WeChat 4.x: db_storage/contact/contact.db, table `contact`.
        // MEASURED: 86,791 rows keyed by `username`, names in `nick_name`/`remark`.
        // The previous code read a table called `user_info`, which does not exist in
        // a real contact.db, so contact names could never resolve.
        var contactDbPath = FindContactDatabasePath();
        if (contactDbPath is not null)
        {
            try
            {
                using var connection = OpenKeyedConnection(contactDbPath);
                ReadContactTable(connection, names);
                ReadChatRoomMembers(connection, names);
                Log.Information("Read {Count} contact name(s) from {ContactDb}", names.Count, contactDbPath);
            }
            catch (Exception ex)
            {
                // Optional by design (R5): never let this fail the connect.
                Log.Warning(ex, "Could not read contact names from {ContactDb}", contactDbPath);
            }
        }
        else
        {
            Log.Warning("No contact database found beside {DbPath}; senders will be labelled with raw wxids", _databasePath);
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
                    // Excludes contact_fts.db: the FTS index beside the real table.
                    candidates.AddRange(Directory.GetFiles(contactDir, "contact*.db")
                        .Where(f => !Path.GetFileName(f).Contains("_fts", StringComparison.OrdinalIgnoreCase)));
                }
                catch (IOException)
                {
                }
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Reads the WeChat 4.x contact name table, <c>contact(username, nick_name,
    /// remark, alias, ...)</c>.
    /// </summary>
    /// <remarks>
    /// MEASURED against the real <c>contact.db</c>: 86,791 rows, none with an empty
    /// <c>username</c>, 131 with neither a nickname nor a remark. The display name
    /// follows WeChat's own rule - remark, then nickname, then the alias - and a row
    /// with none of the three is skipped so the raw wxid is used instead of a blank.
    /// </remarks>
    private static void ReadContactTable(SqliteConnection connection, Dictionary<string, string> names)
    {
        const string table = "contact";
        var columns = GetColumnNames(connection, table);
        if (columns.Count == 0)
            return;

        var identityColumn = FindColumn(columns, "username", "user_name", "user_name_str", "wxid") ?? "username";
        var nickColumn = FindColumn(columns, "nick_name", "nickname", "nick_name_str");
        var remarkColumn = FindColumn(columns, "remark", "remark_name", "con_remark");
        var aliasColumn = FindColumn(columns, "alias");
        if (nickColumn is null && remarkColumn is null && aliasColumn is null)
        {
            Log.Warning("contact table in the contact database has no name column (columns: {Columns})", string.Join(", ", columns));
            return;
        }

        var selectList = string.Join(", ", new[] { identityColumn, nickColumn, remarkColumn, aliasColumn }
            .Where(c => c is not null)
            .Select(c => QuoteIdentifier(c!)));

        using var cmd = new SqliteCommand($"SELECT {selectList} FROM {QuoteIdentifier(table)};", connection);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var identity = reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(identity))
                continue;

            string? Field(int ordinal) =>
                reader.FieldCount > ordinal && !reader.IsDBNull(ordinal)
                    ? Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)?.Trim()
                    : null;

            var nick = Field(1);
            var remark = Field(2);
            var alias = Field(3);

            var display = FirstNonEmpty(remark, nick, alias);
            if (!string.IsNullOrWhiteSpace(display))
                names[identity.Trim()] = display;
        }
    }

    /// <summary>
    /// Adds the groups and group members from <c>chat_room</c> +
    /// <c>chatroom_member</c> (R5).
    /// </summary>
    /// <remarks>
    /// MEASURED on the real contact.db: <c>chat_room(username, owner, ...)</c> has
    /// 906 rows and <c>chatroom_member(room_id, member_id)</c> has 110,089; it joins
    /// the room/member ids to <c>contact.id</c>. On this install every room and every
    /// member already has a row in <c>contact</c>, so this adds ZERO new names - it
    /// exists as the fallback for a database where the direct lookup misses, and the
    /// count is logged so the claim stays checkable.
    /// </remarks>
    private static void ReadChatRoomMembers(SqliteConnection connection, Dictionary<string, string> names)
    {
        var roomColumns = GetColumnNames(connection, "chat_room");
        var memberColumns = GetColumnNames(connection, "chatroom_member");
        var contactColumns = GetColumnNames(connection, "contact");
        if (roomColumns.Count == 0 || memberColumns.Count == 0 || contactColumns.Count == 0)
            return;

        if (!roomColumns.Contains("username", StringComparer.OrdinalIgnoreCase)
            || !memberColumns.Contains("member_id", StringComparer.OrdinalIgnoreCase)
            || !contactColumns.Contains("username", StringComparer.OrdinalIgnoreCase))
            return;

        var before = names.Count;

        // Members of every room, resolved through contact.id -> contact.username.
        const string sql = """
            SELECT r.username AS room, c.username, c.nick_name, c.remark, c.alias
            FROM chat_room r
            JOIN chatroom_member m ON m.room_id = r.id
            JOIN contact c ON c.id = m.member_id
            """;

        try
        {
            using var cmd = new SqliteCommand(sql, connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var member = reader.IsDBNull(1) ? null : reader.GetString(1).Trim();
                if (string.IsNullOrWhiteSpace(member))
                    continue;

                string? Field(int ordinal) =>
                    reader.FieldCount > ordinal && !reader.IsDBNull(ordinal)
                        ? Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)?.Trim()
                        : null;

                if (names.ContainsKey(member))
                    continue;

                var display = FirstNonEmpty(Field(3), Field(2), Field(4));
                if (!string.IsNullOrWhiteSpace(display))
                    names[member] = display;
            }
        }
        catch (SqliteException ex)
        {
            Log.Debug(ex, "Could not read group members from the contact database");
            return;
        }

        Log.Information("Group membership added {Count} contact name(s) not already present", names.Count - before);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
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

    /// <summary>
    /// The <c>Name2Id.rowid -&gt; user_name</c> map of one shard, or null when the
    /// shard has no Name2Id table.
    /// </summary>
    private static Dictionary<long, string>? ReadName2Id(SqliteConnection connection)
    {
        var columns = GetColumnNames(connection, Name2IdTableName);
        if (columns.Count == 0)
        {
            Log.Warning("No {Table} table in a message shard; senders cannot be resolved to user names", Name2IdTableName);
            return null;
        }

        var map = new Dictionary<long, string>();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT rowid, user_name FROM {QuoteIdentifier(Name2IdTableName)};";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(1))
                continue;

            var name = reader.GetString(1);
            if (!string.IsNullOrWhiteSpace(name))
                map[reader.GetInt64(0)] = name.Trim();
        }

        Log.Information("{Table}: {Count} sender id(s) mapped", Name2IdTableName, map.Count);
        return map;
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
    /// This is the single place key handling lives. Every connection site - the
    /// message shards, the session database and the optional contact database - goes
    /// through it, so the derived-raw-key path and the passphrase path cannot drift
    /// apart again (the passphrase used to be applied to the main connection string
    /// only, which left the contact database being read unkeyed).
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

                DisposeConnections();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error disconnecting from database");
        }
        finally
        {
            DisconnectInternal();
        }
    }

    /// <summary>
    /// Closes every opened SQLCipher connection (primary, shards, session, contact)
    /// and clears the cached schema.
    /// </summary>
    private void DisposeConnections()
    {
        _connection?.Dispose();
        _connection = null;

        foreach (var shard in _shards)
        {
            if (!ReferenceEquals(shard.Connection, _connection))
                shard.Connection.Dispose();
        }

        _sessionConnection?.Dispose();
        _sessionConnection = null;
    }

    private void DisconnectInternal()
    {
        _databasePath = null;
        _rawHexKey = null;
        _passphrase = null;
        _appliedKeyDescription = null;

        // Everything discovered about the previous database must go with it, or a
        // later Connect() would read the old schema's tables.
        _tableNames = new List<string>();
        _shards = new List<Shard>();
        _messageTables = new List<string>();
        _messageTableShards = new Dictionary<string, List<Shard>>(StringComparer.OrdinalIgnoreCase);
        _sessionRows = null;
        _sessionRowsLoaded = false;
        _sessionTableColumns = null;
        _sessionDatabasePath = null;
        _contactNames = null;
        _contactNamesLoaded = false;
        _knownIdentities = null;
        _ownerIdentity = null;
        LastResultTruncated = false;
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
