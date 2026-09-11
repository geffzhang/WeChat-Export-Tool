using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Serilog;
using WeChatExport.Core.Models;

namespace WeChatExport.Services;

public class DatabaseService : IDisposable
{
    private SqliteConnection? _connection;
    private string? _databasePath;
    private bool _disposed;

    public bool IsConnected => _connection?.State == System.Data.ConnectionState.Open;

    public string? DatabasePath => _databasePath;

    /// <summary>
    /// Connects to a WeChat database file (MSG.db or MicroMsg.db)
    /// </summary>
    /// <param name="dbPath">Path to the database file</param>
    /// <returns>True if connection successful</returns>
    public bool Connect(string dbPath)
    {
        try
        {
            if (string.IsNullOrEmpty(dbPath))
            {
                Log.Warning("Database path is null or empty");
                return false;
            }

            if (!File.Exists(dbPath))
            {
                Log.Warning("Database file not found: {DbPath}", dbPath);
                return false;
            }

            Disconnect();

            _databasePath = dbPath;
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            _connection = new SqliteConnection(connectionString);
            _connection.Open();

            Log.Information("Connected to WeChat database: {DbPath}", dbPath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to connect to database: {DbPath}", dbPath);
            return false;
        }
    }

    /// <summary>
    /// Connects to the default WeChat MSG database in the user's WeChat data folder
    /// </summary>
    /// <returns>True if connection successful</returns>
    public bool ConnectToDefaultDatabase()
    {
        var defaultPath = GetDefaultMsgDatabasePath();
        if (string.IsNullOrEmpty(defaultPath) || !File.Exists(defaultPath))
        {
            Log.Warning("Default WeChat MSG database not found");
            return false;
        }

        return Connect(defaultPath);
    }

    /// <summary>
    /// Gets the default path to WeChat's MSG database
    /// </summary>
    public string? GetDefaultMsgDatabasePath()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var weChatPath = Path.Combine(appDataPath, "Tencent", "WeChat", "Msg");

        if (Directory.Exists(weChatPath))
        {
            var files = Directory.GetFiles(weChatPath, "Msg*.db");
            if (files.Length > 0)
            {
                // Return the most recent MSG database
                Array.Sort(files, (a, b) => File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)));
                return files[0];
            }
        }

        return null;
    }

    /// <summary>
    /// Gets all contacts from the WeChat database
    /// </summary>
    /// <returns>List of contacts</returns>
    public List<Contact> GetContacts()
    {
        var contacts = new List<Contact>();

        if (_connection == null || !IsConnected)
        {
            Log.Warning("Not connected to database");
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

            Log.Information("Retrieved {Count} contacts from database", contacts.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to retrieve contacts");
        }

        return contacts;
    }

    private string? GetMicroMsgDatabasePath()
    {
        if (string.IsNullOrEmpty(_databasePath))
            return null;

        // MSG.db is in AppData\Roaming\Tencent\WeChat\Msg\
        // MicroMsg.db is in AppData\Roaming\Tencent\WeChat\MicroMsg\
        var msgDir = Path.GetDirectoryName(_databasePath);
        if (string.IsNullOrEmpty(msgDir))
            return null;

        var weChatDir = Path.GetDirectoryName(msgDir);
        if (string.IsNullOrEmpty(weChatDir))
            return null;

        var microMsgDir = Path.Combine(weChatDir, "MicroMsg");
        if (!Directory.Exists(microMsgDir))
            return null;

        // Find the user folder (usually a long hexadecimal folder name)
        var userDirs = Directory.GetDirectories(microMsgDir);
        foreach (var userDir in userDirs)
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
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            using var connection = new SqliteConnection(connectionString);
            connection.Open();

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
            Log.Warning(ex, "Failed to read contacts from MicroMsg.db");
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
            Log.Warning(ex, "Failed to read contacts from MSG.db");
        }

        return contacts;
    }

    /// <summary>
    /// Gets messages from a specific conversation
    /// </summary>
    /// <param name="contactId">The contact/user ID</param>
    /// <param name="limit">Maximum number of messages to retrieve (default 1000)</param>
    /// <returns>List of messages</returns>
    public List<Message> GetMessages(long contactId, int limit = 1000)
    {
        var messages = new List<Message>();

        if (_connection == null || !IsConnected)
        {
            Log.Warning("Not connected to database");
            return messages;
        }

        try
        {
            // Try to get messages from MSG.db
            var query = @"
                SELECT
                    LocalID,
                    CreateTime,
                    IsSender,
                    Content,
                    MessageType,
                    Des,
                    FileName
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
                var message = new Message
                {
                    MessageId = reader.GetInt64(0),
                    CreateTime = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)).LocalDateTime,
                    IsFromSelf = reader.GetInt32(2) == 1,
                    Content = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Type = (MessageType)reader.GetInt32(4),
                    SenderId = reader.GetInt32(2) == 1 ? 0 : contactId
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
        }

        return messages;
    }

    /// <summary>
    /// Gets all messages from the database
    /// </summary>
    /// <param name="limit">Maximum number of messages to retrieve per conversation</param>
    /// <returns>List of all messages</returns>
    public List<Message> GetAllMessages(int limit = 1000)
    {
        var messages = new List<Message>();

        if (_connection == null || !IsConnected)
        {
            Log.Warning("Not connected to database");
            return messages;
        }

        try
        {
            var query = @"
                SELECT
                    LocalID,
                    CreateTime,
                    IsSender,
                    Content,
                    MessageType,
                    Des,
                    FileName,
                    Sender,
                    Receiver
                FROM ChatInfo
                ORDER BY CreateTime DESC
                LIMIT @Limit";

            using var cmd = new SqliteCommand(query, _connection);
            cmd.Parameters.AddWithValue("@Limit", limit);

            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                var message = new Message
                {
                    MessageId = reader.GetInt64(0),
                    CreateTime = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)).LocalDateTime,
                    IsFromSelf = reader.GetInt32(2) == 1,
                    Content = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Type = (MessageType)reader.GetInt32(4)
                };

                // Determine sender ID
                if (!reader.IsDBNull(7))
                {
                    var senderStr = reader.GetString(7);
                    if (long.TryParse(senderStr, out var senderId))
                    {
                        message.SenderId = senderId;
                    }
                }

                // Handle media paths
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

            Log.Information("Retrieved {Count} messages from database", messages.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to retrieve all messages");
        }

        return messages;
    }

    /// <summary>
    /// Gets message count from a specific conversation
    /// </summary>
    /// <param name="contactId">The contact/user ID</param>
    /// <returns>Number of messages</returns>
    public int GetMessageCount(long contactId)
    {
        if (_connection == null || !IsConnected)
        {
            return 0;
        }

        try
        {
            var query = "SELECT COUNT(*) FROM ChatInfo WHERE Sender = @ContactId OR Receiver = @ContactId";
            using var cmd = new SqliteCommand(query, _connection);
            cmd.Parameters.AddWithValue("@ContactId", contactId.ToString());

            var result = cmd.ExecuteScalar();
            return Convert.ToInt32(result);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get message count for contact {ContactId}", contactId);
            return 0;
        }
    }

    /// <summary>
    /// Gets a list of all conversations with their last message info
    /// </summary>
    /// <returns>List of contacts with conversation info</returns>
    public List<Contact> GetConversations()
    {
        var conversations = new List<Contact>();

        if (_connection == null || !IsConnected)
        {
            Log.Warning("Not connected to database");
            return conversations;
        }

        try
        {
            var query = @"
                SELECT
                    CASE
                        WHEN Receiver IS NOT NULL AND Receiver != '' THEN CAST(Receiver AS INTEGER)
                        ELSE 0
                    END as ContactId,
                    MAX(CreateTime) as LastMessageTime,
                    COUNT(*) as MessageCount
                FROM ChatInfo
                WHERE Receiver IS NOT NULL AND Receiver != ''
                GROUP BY ContactId
                ORDER BY LastMessageTime DESC";

            using var cmd = new SqliteCommand(query, _connection);
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                var contactId = reader.GetInt64(0);
                if (contactId > 0)
                {
                    conversations.Add(new Contact
                    {
                        UserId = contactId,
                        LastMessageTime = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)).LocalDateTime
                    });
                }
            }

            Log.Information("Retrieved {Count} conversations", conversations.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to retrieve conversations");
        }

        return conversations;
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
                    _connection.Close();
                    Log.Information("Disconnected from database: {DbPath}", _databasePath);
                }
                _connection.Dispose();
                _connection = null;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error disconnecting from database");
        }
        finally
        {
            _databasePath = null;
        }
    }

    /// <summary>
    /// Executes a raw SQL query and returns results
    /// </summary>
    /// <param name="query">SQL query to execute</param>
    /// <returns>List of dictionaries containing row data</returns>
    public List<Dictionary<string, object?>> ExecuteQuery(string query)
    {
        var results = new List<Dictionary<string, object?>>();

        if (_connection == null || !IsConnected)
        {
            Log.Warning("Not connected to database");
            return results;
        }

        try
        {
            using var cmd = new SqliteCommand(query, _connection);
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                results.Add(row);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to execute query: {Query}", query);
        }

        return results;
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
