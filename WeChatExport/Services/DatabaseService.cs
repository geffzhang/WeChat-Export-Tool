using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>Opened and decrypted, but the tables this app reads are absent.</summary>
    MissingExpectedTables,

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
    /// Tables this app actually reads out of a MSG database. A real WeChat MSG*.db
    /// uses different table names, so a mismatch surfaces as
    /// <see cref="ConnectOutcome.MissingExpectedTables"/> rather than a silent
    /// "0 contacts" - see the honest-failure handling in Connect().
    /// </summary>
    private static readonly string[] ExpectedMsgTables = { "ChatInfo" };

    private SqliteConnection? _connection;
    private string? _databasePath;
    private string? _rawHexKey;
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
    /// Optional decryption key. A 64-hex-character value is treated as WeChat's raw
    /// 32-byte SQLCipher key; anything else is treated as a passphrase.
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
            // WeChat MSG*.db files are SQLCipher v4 databases whose key is a raw
            // 32-byte value, normally written as 64 hex characters. The matching
            // derivation is PBKDF2-HMAC-SHA512(rawKey, dbSalt, 256000) - which is
            // exactly what SQLCipher does with a raw key, and NOT what it does with
            // a passphrase. So a 64-hex key must go through SQLCipher's raw-key
            // syntax, issued as the first statement on the connection:
            //     PRAGMA key = "x'<64 hex chars>'"
            // Only non-hex input is treated as a passphrase, via
            // SqliteConnectionStringBuilder.Password (which maps to sqlite3_key with
            // text semantics). Passing a hex WeChat key as Password silently derives
            // a different key and fails with SQLITE_NOTADB - verified in the fix
            // harness, so it is not merely a theoretical concern.
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            };

            _rawHexKey = null;
            if (hasKey)
            {
                if (CryptoUtils.TryNormalizeHexKey(key, out var normalizedHex))
                {
                    _rawHexKey = normalizedHex;
                }
                else
                {
                    connectionString.Password = key;
                }

                // A keyed connection must NOT be pooled. The raw-hex key is applied
                // by a PRAGMA, so it is not part of the connection string and
                // therefore not part of Microsoft.Data.Sqlite's pool key. A pooled
                // connection keeps whichever key it was first opened with, so a
                // later Connect() with a different (even wrong) key would silently
                // reuse it and appear to succeed. Verified in the fix harness.
                connectionString.Pooling = false;
            }

            _connection = new SqliteConnection(connectionString.ToString());
            _connection.Open();

            if (_rawHexKey is not null)
            {
                using var keyCommand = _connection.CreateCommand();
                keyCommand.CommandText = $"PRAGMA key = \"x'{_rawHexKey}'\";";
                keyCommand.ExecuteNonQuery();
            }

            // Opening proves nothing on its own: SQLite defers reading the header
            // until the first statement, so a wrong key or a non-database file only
            // fails here. Probe cheaply and report honestly.
            //
            // Depending on the mechanism, a bad key surfaces either at Open() (the
            // connection-string Password keyword is validated eagerly) or at this
            // first read (the raw-hex PRAGMA form is not), so both are covered by the
            // SQLITE_NOTADB catch clauses around this whole block.
            using (var probe = _connection.CreateCommand())
            {
                probe.CommandText = "SELECT count(*) FROM sqlite_master;";
                probe.ExecuteScalar();
            }

            using (var tableProbe = _connection.CreateCommand())
            {
                tableProbe.CommandText =
                    "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN (" +
                    string.Join(", ", ExpectedMsgTables.Select((_, i) => $"@t{i}")) + ");";

                for (var i = 0; i < ExpectedMsgTables.Length; i++)
                    tableProbe.Parameters.AddWithValue($"@t{i}", ExpectedMsgTables[i]);

                if (Convert.ToInt32(tableProbe.ExecuteScalar()) == 0)
                {
                    Log.Warning(
                        "Connected to {DbPath} but found none of the expected tables ({Tables})",
                        dbPath,
                        string.Join(", ", ExpectedMsgTables));
                    DisposeConnection();

                    return new ConnectResult(
                        ConnectOutcome.MissingExpectedTables,
                        $"The database opened, but it has none of the tables this app reads ({string.Join(", ", ExpectedMsgTables)}). The schema is probably a different WeChat version.");
                }
            }

            _databasePath = dbPath;
            Log.Information("Connected to WeChat database: {DbPath} (key applied: {HasKey})", dbPath, hasKey);
            return new ConnectResult(ConnectOutcome.Success, "Connected successfully");
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
                    "The database could not be decrypted with the supplied key. Check the key, or clear it if the file is not encrypted.")
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
            using var connection = CreateConnection(dbPath);

            // Try to get contacts from MicroMsg.db
            // Contact table structure varies by WeChat version
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
                        var contact = new Contact
                        {
                            UserId = reader.GetInt64(0),
                            NickName = reader.IsDBNull(1) ? null : reader.GetString(1),
                            Remark = reader.IsDBNull(2) ? null : reader.GetString(2)
                        };
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

        try
        {
            // In MSG.db, contacts are stored in the ChatInfo table or similar
            var query = @"
                SELECT DISTINCT
                    CASE
                        WHEN Sender IS NOT NULL AND Sender != '' THEN CAST(Sender AS INTEGER)
                        ELSE 0
                    END as UserId,
                    '' as NickName,
                    '' as Remark
                FROM ChatInfo
                WHERE Sender IS NOT NULL AND Sender != ''
                UNION
                SELECT DISTINCT
                    CASE
                        WHEN Receiver IS NOT NULL AND Receiver != '' THEN CAST(Receiver AS INTEGER)
                        ELSE 0
                    END as UserId,
                    '' as NickName,
                    '' as Remark
                FROM ChatInfo
                WHERE Receiver IS NOT NULL AND Receiver != ''";

            using var cmd = new SqliteCommand(query, _connection);
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                var userId = reader.GetInt64(0);
                if (userId > 0)
                {
                    contacts.Add(new Contact
                    {
                        UserId = userId,
                        NickName = reader.IsDBNull(1) ? null : reader.GetString(1),
                        Remark = reader.IsDBNull(2) ? null : reader.GetString(2)
                    });
                }
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
    /// <param name="contactId">The contact/user ID.</param>
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
        long contactId,
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
            cmd.Parameters.AddWithValue("@ContactId", contactId.ToString());
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
                    SenderId = isFromSelf ? 0 : contactId,
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

            Log.Information("Retrieved {Count} messages for contact {ContactId}", messages.Count, contactId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to retrieve messages for contact {ContactId}", contactId);
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
    /// Builds a connection to <paramref name="dbPath"/>, applying the same key
    /// (raw-hex pragma or passphrase) that was used for the main connection.
    /// </summary>
    private SqliteConnection CreateConnection(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        };

        if (_rawHexKey is not null)
        {
            // See Connect(): the key is applied by PRAGMA, so the connection must not
            // be pooled or a differently-keyed open could reuse it.
            builder.Pooling = false;
        }

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        if (_rawHexKey is not null)
        {
            using var keyCommand = connection.CreateCommand();
            keyCommand.CommandText = $"PRAGMA key = \"x'{_rawHexKey}'\";";
            keyCommand.ExecuteNonQuery();
        }

        return connection;
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
