using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
    /// Opened and decrypted, but the tables this app reads are absent. The message
    /// lists the tables that <em>were</em> found so the mismatch is diagnosable
    /// instead of being a dead end.
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
    /// Tables this app actually reads out of a MSG database. A real WeChat MSG*.db
    /// uses different table names, so a mismatch surfaces as
    /// <see cref="ConnectOutcome.SchemaMismatch"/> (naming the tables that were
    /// found) rather than a silent "0 contacts" - see the honest-failure handling
    /// in Connect().
    /// </summary>
    private static readonly string[] ExpectedMsgTables = { "ChatInfo" };

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
    /// Connects to a WeChat database file (MSG.db or MicroMsg.db).
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
            // WeChat MSG*.db files are SQLCipher databases. WeChat hands SQLCipher
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

            // A schema mismatch must still be reported as a failure - it must never
            // light a green "Connected" over an empty contact list. But it must also
            // be diagnosable: with no real WeChat database available while this app
            // is being built, the table list is the artifact needed to finish the
            // real schema mapping, so name it instead of stopping at "wrong version".
            var tables = GetTableNames(_connection);
            var missingTables = ExpectedMsgTables
                .Where(expected => !tables.Contains(expected, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (missingTables.Count > 0)
            {
                Log.Warning(
                    "Opened {DbPath} but it is missing the expected table(s) {MissingTables}. Full table list ({TableCount}): {Tables}",
                    dbPath,
                    string.Join(", ", missingTables),
                    tables.Count,
                    string.Join(", ", tables));
                DisposeConnection();

                return new ConnectResult(
                    ConnectOutcome.SchemaMismatch,
                    BuildSchemaMismatchMessage(missingTables, tables));
            }

            _databasePath = dbPath;
            Log.Information(
                "Connected to WeChat database: {DbPath} (key applied: {HasKey}, {KeySource})",
                dbPath,
                hasKey,
                _appliedKeyDescription);
            return new ConnectResult(ConnectOutcome.Success, "Connected successfully" + FormatKeySuffix());
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
    /// Builds the schema-mismatch message: which expected table was missing, and
    /// which tables were actually found (capped for readability - the full list is
    /// logged at Warning by the caller).
    /// </summary>
    private static string BuildSchemaMismatchMessage(
        IReadOnlyList<string> missingTables,
        IReadOnlyList<string> foundTables)
    {
        var shown = foundTables.Count == 0
            ? "(none - the database contains no tables at all)"
            : string.Join(", ", foundTables.Take(DiscoveredTableListLimit));

        var truncated = foundTables.Count > DiscoveredTableListLimit
            ? $", ... ({foundTables.Count} tables in total)"
            : string.Empty;

        return $"The database opened and decrypted, but it is missing the table(s) this app reads "
             + $"({string.Join(", ", missingTables)}). Tables actually found: {shown}{truncated}. "
             + "This build does not know this WeChat schema yet - please report the table list above.";
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
    /// Gets all contacts from the WeChat database.
    /// </summary>
    /// <returns>List of contacts</returns>
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
            // Try to get contacts from MicroMsg.db (the contact database)
            var microMsgPath = GetMicroMsgDatabasePath();
            if (!string.IsNullOrEmpty(microMsgPath) && File.Exists(microMsgPath))
            {
                contacts = GetContactsFromMicroMsg(microMsgPath);
            }

            // Also try to get contacts from current MSG.db if no contacts found
            if (contacts.Count == 0 && !string.IsNullOrEmpty(_databasePath))
            {
                contacts = GetContactsFromMsgDb(_databasePath);
            }

            if (contacts.Count == 0 && LastError is null)
            {
                LastError = "The database contains no readable contacts in the expected tables.";
            }

            Log.Information("Retrieved {Count} contacts from database", contacts.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to retrieve contacts");
            LastError = $"Failed to read contacts: {ex.Message}";
        }

        return contacts;
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

    private List<Contact> GetContactsFromMicroMsg(string dbPath)
    {
        var contacts = new List<Contact>();

        try
        {
            // MicroMsg.db is itself a SQLCipher database, so it needs the same key.
            // OpenKeyedConnection() is the single keyed-open path shared with the main
            // connection, so this sibling database can never be read unkeyed.
            using var connection = OpenKeyedConnection(dbPath);

            // Try to get contacts from MicroMsg.db
            // UNVERIFIED AGAINST A REAL DATABASE: the table names and especially the
            // DisplayName/Avatar columns are assumptions carried over from the
            // original import; a real MicroMsg.db Contact table is not known to
            // expose them. Kept as-is rather than replaced with a guess.
            var tables = new[] { "Contact", "Contact_V2" };

            foreach (var table in tables)
            {
                try
                {
                    var query = $"SELECT UserID, NickName, Remark, DisplayName, Avatar FROM {table}";
                    using var cmd = new SqliteCommand(query, connection);
                    using var reader = cmd.ExecuteReader();

                    while (reader.Read())
                    {
                        // UserID is read as text on purpose: in a real WeChat database
                        // it is a wxid. Casting it to a number turned it into 0, which
                        // is why per-sender lookups could never match (see Contact.Identifier).
                        var identifier = reader.IsDBNull(0)
                            ? null
                            : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);

                        var contact = new Contact
                        {
                            Identifier = string.IsNullOrWhiteSpace(identifier) ? null : identifier.Trim(),
                            NickName = reader.IsDBNull(1) ? null : reader.GetString(1),
                            Remark = reader.IsDBNull(2) ? null : reader.GetString(2)
                        };

                        // Numeric ids are still populated when the identifier happens to
                        // be numeric (the synthetic schema), so Message queries that key
                        // off UserId keep working.
                        if (contact.Identifier is not null
                            && long.TryParse(contact.Identifier, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
                        {
                            contact.UserId = userId;
                        }

                        contacts.Add(contact);
                    }

                    if (contacts.Count > 0)
                        break;
                }
                catch (SqliteException)
                {
                    // Table doesn't exist or query failed, try next table
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read contacts from MicroMsg.db");
            LastError = $"Failed to read contacts from MicroMsg.db: {ex.Message}";
        }

        return contacts;
    }

    private List<Contact> GetContactsFromMsgDb(string dbPath)
    {
        var contacts = new List<Contact>();
        var identifiers = new List<string>();

        try
        {
            // UNVERIFIED AGAINST A REAL DATABASE: "ChatInfo" and the Sender/Receiver
            // columns below are the synthetic schema this app was built around. A
            // real WeChat MSG*.db uses different table and column names, and this
            // app does not know them yet - guessing replacements would be worse than
            // this comment, so the real names are deliberately not invented here.
            // Contacts should come from MicroMsg.db; this is only the fallback.
            //
            // The previous version of this method CAST(Sender AS INTEGER) and then
            // kept only ids > 0. A real Sender value is a wxid (text), so the CAST
            // produced 0 and every row was discarded - and the rows that did survive
            // came with '' names, which is what turned every message into "Unknown".
            // Sender/Receiver are therefore read as text, exactly as stored.
            var query = @"
                SELECT DISTINCT Sender AS Identifier FROM ChatInfo
                WHERE Sender IS NOT NULL AND Sender != ''
                UNION
                SELECT DISTINCT Receiver FROM ChatInfo
                WHERE Receiver IS NOT NULL AND Receiver != ''";

            using (var cmd = new SqliteCommand(query, _connection))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var identifier = reader.IsDBNull(0)
                        ? null
                        : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);

                    if (string.IsNullOrWhiteSpace(identifier))
                        continue;

                    // Only the identifiers are discoverable here: this table carries
                    // no name column in the schema assumed above. Emitting a contact
                    // per identifier would rebuild exactly the bogus contact list this
                    // fix removes (empty name, nothing to show), so gather them as a
                    // diagnostic instead of returning placeholders.
                    identifiers.Add(identifier.Trim());
                }
            }

            if (identifiers.Count > 0)
            {
                Log.Warning(
                    "MSG.db {DbPath} references {Count} sender identifier(s) but exposes no contact name column we can read. Identifiers: {Identifiers}",
                    dbPath,
                    identifiers.Count,
                    string.Join(", ", identifiers.Take(DiscoveredTableListLimit)));

                LastError =
                    $"Found {identifiers.Count} sender identifier(s) in {Path.GetFileName(dbPath)} "
                  + $"({string.Join(", ", identifiers.Take(DiscoveredTableListLimit))}) but no contact-name column, "
                  + "so no contacts could be built from this database. Names have to come from MicroMsg.db; "
                  + "the MSG database's own contact schema is not mapped yet.";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read contacts from MSG.db");
            LastError = $"Failed to read contacts from MSG.db: {ex.Message}";
        }

        return contacts;
    }

    /// <summary>
    /// Gets messages from a specific conversation.
    /// </summary>
    /// <param name="contactIdentifier">
    /// The contact's identifier exactly as the database stores it: a wxid such as
    /// <c>wxid_abc123</c> in a real WeChat database, or a numeric id in a synthetic
    /// one. It is deliberately a <see cref="string"/> rather than a <c>long</c>: the
    /// numeric view (<see cref="Contact.UserId"/>) is <c>0</c> for every real
    /// contact, so passing it filtered on <c>'0'</c> and a real conversation came
    /// back empty. The caller cannot express "the numeric id" here any more - only
    /// the identifier the database actually holds (see <see cref="Contact.Identifier"/>).
    /// </param>
    /// <param name="limit">Maximum number of messages to retrieve (default 1000).</param>
    /// <param name="conversationDisplayName">
    /// Display name of the conversation's contact. Used to fill in
    /// <see cref="Message.SenderName"/> for the other party, so the UI and every
    /// export show a real name instead of a blank/"Unknown" sender.
    /// </param>
    /// <param name="senderNames">
    /// Optional per-sender lookup (sender identifier as stored in the database,
    /// e.g. a wxid, mapped to a contact display name). When a message's sender is
    /// present here it wins over <paramref name="conversationDisplayName"/>; this is
    /// what lets a group conversation attribute each message to the right person.
    /// </param>
    /// <returns>List of messages</returns>
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
            Log.Warning("GetMessages called without a contact identifier");
            LastError = "No contact identifier was supplied, so no conversation could be selected.";
            return messages;
        }

        // SenderId is the legacy numeric view of the conversation partner. It is
        // only meaningful when the identifier really is numeric (the synthetic
        // schema); a wxid has no numeric form, so it stays 0 while
        // Message.SenderIdentifier carries the value the database actually holds.
        var senderId = long.TryParse(identifier, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericId)
            ? numericId
            : 0L;

        try
        {
            // Try to get messages from MSG.db. Sender is selected so the sender can
            // be resolved to a contact name rather than left blank.
            var query = @"
                SELECT
                    LocalID,
                    CreateTime,
                    IsSender,
                    Content,
                    MessageType,
                    Des,
                    FileName,
                    Sender
                FROM ChatInfo
                WHERE (Sender = @ContactId OR Receiver = @ContactId)
                ORDER BY CreateTime DESC
                LIMIT @Limit";

            using var cmd = new SqliteCommand(query, _connection);
            cmd.Parameters.AddWithValue("@ContactId", identifier);
            cmd.Parameters.AddWithValue("@Limit", limit);

            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                var isFromSelf = reader.GetInt32(2) == 1;

                var message = new Message
                {
                    MessageId = reader.GetInt64(0),
                    CreateTime = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)).LocalDateTime,
                    IsFromSelf = isFromSelf,
                    Content = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Type = (MessageType)reader.GetInt32(4),
                    SenderId = isFromSelf ? 0 : senderId,
                    // The sender exactly as stored (a wxid in a real database). Kept so
                    // the exports can attribute a message to a stable identifier when no
                    // display name could be resolved, instead of printing "Unknown".
                    SenderIdentifier = reader.IsDBNull(7) ? null : reader.GetString(7),
                    // Only the other party needs a name: the UI and the exports render
                    // self-authored messages as "You".
                    SenderName = isFromSelf
                        ? null
                        : ResolveSenderName(
                            reader.IsDBNull(7) ? null : reader.GetString(7),
                            conversationDisplayName,
                            senderNames)
                };

                // Handle message type-specific data (media paths, etc.)
                if (!reader.IsDBNull(5))
                {
                    var des = reader.GetString(5);
                    if (!string.IsNullOrEmpty(des))
                    {
                        message.MediaPath = des;
                    }
                }

                messages.Add(message);
            }

            // Reverse to get chronological order
            messages.Reverse();

            Log.Information("Retrieved {Count} messages for contact {ContactIdentifier}", messages.Count, identifier);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to retrieve messages for contact {ContactIdentifier}", identifier);
            LastError = $"Failed to read messages: {ex.Message}";
        }

        return messages;
    }

    /// <summary>
    /// Picks the best available display name for a message's sender.
    /// </summary>
    /// <remarks>
    /// Preference order: an explicit per-sender mapping first (accurate for group
    /// chats), then the conversation's contact name. The fallback matters because
    /// without it every message from the other party renders as blank/"Unknown".
    /// </remarks>
    private static string? ResolveSenderName(
        string? senderId,
        string? conversationDisplayName,
        IReadOnlyDictionary<string, string>? senderNames)
    {
        if (senderNames is not null
            && !string.IsNullOrEmpty(senderId)
            && senderNames.TryGetValue(senderId, out var mapped)
            && !string.IsNullOrWhiteSpace(mapped))
        {
            return mapped;
        }

        return string.IsNullOrWhiteSpace(conversationDisplayName) ? null : conversationDisplayName;
    }

    /// <summary>
    /// Opens <paramref name="dbPath"/> applying the key material captured by
    /// <see cref="Connect"/>.
    /// </summary>
    /// <remarks>
    /// This is the single place key handling lives. Both connection sites - the main
    /// MSG database and the sibling <c>MicroMsg.db</c> - go through it, so the
    /// derived-raw-key path and the passphrase path cannot drift apart again (the
    /// passphrase used to be applied to the main connection string only, which left
    /// MicroMsg.db being read unkeyed).
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
