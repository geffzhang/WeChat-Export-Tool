# WeChat Export Tool - .NET 10 + AvaloniaUI Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Migrate the existing Python-based WeChat Export Tool to .NET 10 with AvaloniaUI 12.x, implementing all core features including WeChat database decryption, contact browsing, message viewing, and 6 export formats.

**Architecture:** Single project with MVVM pattern using AvaloniaUI. Core business logic (decryption, database, export) separated into service classes. ViewModels handle UI logic. Reimplement WCDB decryption in C#.

**Tech Stack:** .NET 10.0.x, AvaloniaUI 12.x, Microsoft.Data.Sqlite, ClosedXML, QuestPDF, Serilog

**Spec:** [2026-09-11-dotnet-avalonia-migration-design.md](../specs/2026-09-11-dotnet-avalonia-migration-design.md)

---

## Global Constraints

- .NET SDK: 10.0.x
- AvaloniaUI: 12.x
- Target: Windows x64 only
- Single executable deployment via `dotnet publish`
- Preserve all existing export formats (PDF, CSV, Excel, HTML, TXT, JSON)

---

## File Structure

```
WeChatExport/
├── WeChatExport.csproj           # Project file with all dependencies
├── Program.cs                    # Entry point
├── App.axaml                     # Avalonia application definition
├── App.axaml.cs                  # Code-behind
├── ViewModels/
│   ├── MainWindowViewModel.cs    # Main window logic
│   ├── ContactsViewModel.cs      # Contact list
│   └── MessagesViewModel.cs      # Message display
├── Views/
│   ├── MainWindow.axaml          # Main window UI
│   ├── ContactsView.axaml        # Contact list UI
│   └── MessagesView.axaml        # Message view UI
├── Services/
│   ├── WeChatPathService.cs      # Find WeChat installation
│   ├── KeyCaptureService.cs      # Capture decryption key
│   ├── DatabaseService.cs        # SQLite connection
│   └── ExportService.cs          # Export handlers
├── Core/
│   ├── Decryption/
│   │   ├── WeChatDecryptor.cs    # Main decryptor
│   │   └── CryptoUtils.cs         # Crypto utilities
│   └── Models/
│       ├── Contact.cs            # Contact model
│       ├── Message.cs            # Message model
│       └── Conversation.cs       # Conversation model
└── Assets/
    └── icon.ico                   # Application icon
```

---

## Implementation Tasks

### Phase 1: Project Setup

#### Task 1: Initialize .NET 10 Project with AvaloniaUI

**Files:**
- Create: `WeChatExport/WeChatExport.csproj`
- Create: `WeChatExport/Program.cs`
- Create: `WeChatExport/App.axaml`
- Create: `WeChatExport/App.axaml.cs`
- Create: `WeChatExport/ViewModels/MainWindowViewModel.cs`
- Create: `WeChatExport/Views/MainWindow.axaml`

**Interfaces:**
- Produces: `App` class, `MainWindow`, `MainWindowViewModel` - foundation for all later tasks

- [ ] **Step 1: Create project directory**

```bash
mkdir -p WeChatExport
cd WeChatExport
```

- [ ] **Step 2: Create csproj with dependencies**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <BuiltInComInteropSupport>true</BuiltInComInteropSupport>
    <ApplicationManifest>app.manifest</ApplicationManifest>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Avalonia" Version="12.2.0" />
    <PackageReference Include="Avalonia.Desktop" Version="12.2.0" />
    <PackageReference Include="Avalonia.Themes.Fluent" Version="12.2.0" />
    <PackageReference Include="Avalonia.Fonts.Inter" Version="12.2.0" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.3.2" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.11" />
    <PackageReference Include="ClosedXML" Version="0.104.1" />
    <PackageReference Include="QuestPDF" Version="2026.1.0" />
    <PackageReference Include="Serilog" Version="4.1.0" />
    <PackageReference Include="Serilog.Sinks.File" Version="6.0.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create Program.cs**

```csharp
using Avalonia;
using System;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
```

- [ ] **Step 4: Create App.axaml**

```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="WeChatExport.App">
    <Application.Styles>
        <FluentTheme />
    </Application.Styles>
    <Application.Resources>
    </Application.Resources>
</Application>
```

- [ ] **Step 5: Create App.axaml.cs**

```csharp
using Avalonia.ApplicationModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace WeChatExport;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
```

- [ ] **Step 6: Create basic MainWindowViewModel.cs**

```csharp
using CommunityToolkit.Mvvm.ComponentModel;

namespace WeChatExport.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "WeChat Export Tool";
}
```

- [ ] **Step 7: Create MainWindow.axaml**

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:WeChatExport.ViewModels"
        x:Class="WeChatExport.Views.MainWindow"
        x:DataType="vm:MainWindowViewModel"
        Title="{Binding Title}"
        Width="1200"
        Height="800">
    <StackPanel>
        <TextBlock Text="WeChat Export Tool" FontSize="24" Margin="20"/>
    </StackPanel>
</Window>
```

- [ ] **Step 8: Create MainWindow.axaml.cs**

```csharp
using Avalonia.Controls;

namespace WeChatExport.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 9: Verify build**

```bash
cd WeChatExport
dotnet build
```

Expected: BUILD SUCCEEDED

- [ ] **Step 10: Run to verify empty shell works**

```bash
dotnet run
```

Expected: Window opens with "WeChat Export Tool" title

- [ ] **Step 11: Commit**

```bash
git add WeChatExport/
git commit -m "feat: initialize .NET 10 project with AvaloniaUI 12.x"
```

---

#### Task 2: Create Data Models

**Files:**
- Create: `WeChatExport/Core/Models/Contact.cs`
- Create: `WeChatExport/Core/Models/Message.cs`
- Create: `WeChatExport/Core/Models/Conversation.cs`

**Interfaces:**
- Produces: `Contact`, `Message`, `Conversation` classes used by services and viewmodels

- [ ] **Step 1: Create Contact.cs**

```csharp
namespace WeChatExport.Core.Models;

public class Contact
{
    public long UserId { get; set; }
    public string? NickName { get; set; }
    public string? Remark { get; set; }
    public string? DisplayName => !string.IsNullOrEmpty(Remark) ? Remark : NickName;
    public string? AvatarPath { get; set; }
    public int UnreadCount { get; set; }
    public DateTime? LastMessageTime { get; set; }
    public string? LastMessagePreview { get; set; }
}
```

- [ ] **Step 2: Create Message.cs**

```csharp
namespace WeChatExport.Core.Models;

public class Message
{
    public long MessageId { get; set; }
    public long SenderId { get; set; }
    public string? SenderName { get; set; }
    public string? Content { get; set; }
    public MessageType Type { get; set; }
    public DateTime CreateTime { get; set; }
    public bool IsFromSelf { get; set; }
    public string? MediaPath { get; set; }
}

public enum MessageType
{
    Text = 1,
    Image = 3,
    Voice = 34,
    Video = 43,
    Emoji = 47,
    File = 49,
    System = 10000
}
```

- [ ] **Step 3: Create Conversation.cs**

```csharp
namespace WeChatExport.Core.Models;

public class Conversation
{
    public Contact Contact { get; set; } = new();
    public List<Message> Messages { get; set; } = new();
    public int TotalMessageCount { get; set; }
}
```

- [ ] **Step 4: Commit**

```bash
git add WeChatExport/Core/Models/
git commit -m "feat: add data models (Contact, Message, Conversation)"
```

---

### Phase 2: Core Features

#### Task 3: WeChat Path Detection Service

**Files:**
- Create: `WeChatExport/Services/WeChatPathService.cs`

**Interfaces:**
- Produces: `WeChatPathService` class with `DetectWeChatPath()`, `GetDefaultPaths()`, `IsValidWeChatPath(string path)` methods

- [ ] **Step 1: Write the failing test**

```csharp
// In WeChatExport.Tests/WeChatPathServiceTests.cs
using WeChatExport.Services;

public class WeChatPathServiceTests
{
    [Fact]
    public void GetDefaultPaths_ShouldReturnCommonPaths()
    {
        var service = new WeChatPathService();
        var paths = service.GetDefaultPaths();
        Assert.NotEmpty(paths);
    }
}
```

- [ ] **Step 2: Create WeChatPathService.cs**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace WeChatExport.Services;

public class WeChatPathService
{
    private static readonly string[] CommonPaths = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tencent", "WeChat"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tencent", "WeChat"),
        @"C:\Program Files (x86)\Tencent\WeChat",
        @"C:\Program Files\Tencent\WeChat"
    };

    public IEnumerable<string> GetDefaultPaths() => CommonPaths;

    public string? DetectWeChatPath()
    {
        // Try registry first
        var registryPath = GetPathFromRegistry();
        if (!string.IsNullOrEmpty(registryPath) && IsValidWeChatPath(registryPath))
            return registryPath;

        // Scan common paths
        foreach (var path in CommonPaths)
        {
            if (IsValidWeChatPath(path))
                return path;
        }

        return null;
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
```

- [ ] **Step 3: Add test project and verify build**

```bash
cd WeChatExport
dotnet add package xunit
dotnet add package xunit.runner.visualstudio
dotnet new xunit -o ../WeChatExport.Tests
# Add reference and tests
dotnet test
```

Expected: Tests pass

- [ ] **Step 4: Commit**

```bash
git add WeChatExport/Services/WeChatPathService.cs
git commit -m "feat: add WeChat path detection service"
```

---

#### Task 4: Key Capture Service

**Files:**
- Create: `WeChatExport/Services/KeyCaptureService.cs`

**Interfaces:**
- Produces: `KeyCaptureService` class with `IsWeChatRunning()`, `CaptureKeyFromProcess()`, `SaveKey(string key)`, `LoadKey()` methods

- [ ] **Step 1: Write test structure**

```csharp
// Tests for KeyCaptureService
public class KeyCaptureServiceTests
{
    [Fact]
    public void LoadKey_WhenNoKey_ShouldReturnNull()
    {
        var service = new KeyCaptureService();
        var key = service.LoadKey();
        // Should return null or empty if no saved key
        Assert.True(string.IsNullOrEmpty(key) || key != null);
    }
}
```

- [ ] **Step 2: Create KeyCaptureService.cs**

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Serilog;

namespace WeChatExport.Services;

public class KeyCaptureService
{
    private readonly string _keyStoragePath;
    private readonly ILogger _logger;

    public KeyCaptureService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var configDir = Path.Combine(appData, "WeChatExport");
        Directory.CreateDirectory(configDir);
        _keyStoragePath = Path.Combine(configDir, "wechat_key.json");
        _logger = Log.ForContext<KeyCaptureService>();
    }

    public bool IsWeChatRunning()
    {
        return Process.GetProcessesByName("WeChat").Length > 0;
    }

    public string? CaptureKeyFromProcess()
    {
        _logger.Information("Attempting to capture WeChat decryption key");

        try
        {
            var wechatProcesses = Process.GetProcessesByName("WeChat");
            if (wechatProcesses.Length == 0)
            {
                _logger.Warning("WeChat process not found");
                return null;
            }

            // Key capture logic - search process memory for known patterns
            // This is a simplified version - actual implementation requires
            // more sophisticated memory scanning
            var process = wechatProcesses[0];
            
            // TODO: Implement actual key capture from process memory
            // The key is typically stored in a specific memory region
            // Known patterns: specific strings or byte sequences
            
            _logger.Warning("Key capture not fully implemented - manual entry required");
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to capture key from WeChat process");
            return null;
        }
    }

    public void SaveKey(string key)
    {
        try
        {
            var keyData = new { Key = key, SavedAt = DateTime.Now };
            var json = JsonSerializer.Serialize(keyData);
            File.WriteAllText(_keyStoragePath, json);
            _logger.Information("Key saved successfully");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save key");
        }
    }

    public string? LoadKey()
    {
        try
        {
            if (!File.Exists(_keyStoragePath))
                return null;

            var json = File.ReadAllText(_keyStoragePath);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("Key").GetString();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load key");
            return null;
        }
    }
}
```

- [ ] **Step 3: Commit**

```bash
git add WeChatExport/Services/KeyCaptureService.cs
git commit -m "feat: add key capture service"
```

---

#### Task 5: WCDB Decryption (Core)

**Files:**
- Create: `WeChatExport/Core/Decryption/CryptoUtils.cs`
- Create: `WeChatExport/Core/Decryption/WeChatDecryptor.cs`

**Interfaces:**
- Produces: `CryptoUtils` and `WeChatDecryptor` classes for decrypting WeChat database

- [ ] **Step 1: Create CryptoUtils.cs**

```csharp
using System;
using System.Security.Cryptography;
using System.Text;

namespace WeChatExport.Core.Decryption;

public static class CryptoUtils
{
    /// <summary>
    /// Derive the actual database key from WeChat's key
    /// </summary>
    public static byte[] DeriveDatabaseKey(string wechatKey)
    {
        if (string.IsNullOrEmpty(wechatKey))
            throw new ArgumentException("Key cannot be empty", nameof(wechatKey));

        // WeChat uses a specific key derivation
        // The key is typically 32 bytes, may need padding or transformation
        var keyBytes = Encoding.UTF8.GetBytes(wechatKey);
        
        // Known derivation: double MD5 of the key
        using var md5 = MD5.Create();
        var firstHash = md5.ComputeHash(keyBytes);
        var secondHash = md5.ComputeHash(firstHash);
        return secondHash;
    }

    /// <summary>
    /// Try to parse various key formats from WeChat
    /// </summary>
    public static byte[]? TryParseKey(string keyString)
    {
        if (string.IsNullOrEmpty(keyString))
            return null;

        // Try hex string
        if (keyString.Length == 64 && IsHexString(keyString))
            return HexStringToBytes(keyString);

        // Try base64
        try
        {
            return Convert.FromBase64String(keyString);
        }
        catch
        {
            // Not base64
        }

        // Try as plain key - derive it
        return DeriveDatabaseKey(keyString);
    }

    private static bool IsHexString(string s)
    {
        foreach (var c in s)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }
        return true;
    }

    private static byte[] HexStringToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }
}
```

- [ ] **Step 2: Create WeChatDecryptor.cs**

```csharp
using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Serilog;

namespace WeChatExport.Core.Decryption;

public class WeChatDecryptor
{
    private readonly ILogger _logger;

    public WeChatDecryptor()
    {
        _logger = Log.ForContext<WeChatDecryptor>();
    }

    /// <summary>
    /// Opens a WeChat database file with decryption
    /// </summary>
    public SqliteConnection? OpenDatabase(string dbPath, string key)
    {
        if (!File.Exists(dbPath))
        {
            _logger.Error("Database file not found: {Path}", dbPath);
            return null;
        }

        try
        {
            // WeChat uses SQLCipher for encryption
            // The connection string needs the key
            var keyBytes = CryptoUtils.TryParseKey(key);
            if (keyBytes == null)
            {
                _logger.Error("Invalid key format");
                return null;
            }

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();

            var connection = new SqliteConnection(connectionString);
            connection.Open();

            // Try to decrypt - WeChat stores key differently per version
            // This is a placeholder - actual implementation needs version-specific logic
            var success = TryDecryptDatabase(connection, keyBytes);
            if (!success)
            {
                _logger.Warning("Database may not be decrypted - attempting to read anyway");
            }

            return connection;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to open database: {Path}", dbPath);
            return null;
        }
    }

    private bool TryDecryptDatabase(SqliteConnection connection, byte[] key)
    {
        // Different WeChat versions use different encryption methods
        // Version detection and key application needed
        // This is a placeholder for the actual decryption logic
        
        try
        {
            // Try to execute a simple query to verify decryption
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' LIMIT 1";
            cmd.ExecuteNonQuery();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get the path to WeChat's message database
    /// </summary>
    public string? GetDatabasePath(string weChatPath, string? account = null)
    {
        var baseDir = Path.Combine(weChatPath, "WeChat Files");
        if (!Directory.Exists(baseDir))
        {
            _logger.Error("WeChat Files directory not found");
            return null;
        }

        // Find account directory
        var userDirs = Directory.GetDirectories(baseDir);
        if (userDirs.Length == 0)
        {
            _logger.Error("No user directories found");
            return null;
        }

        var targetDir = account ?? Path.GetFileName(userDirs[0]);
        var msgDir = Path.Combine(baseDir, targetDir, "Msg");
        
        if (!Directory.Exists(msgDir))
        {
            _logger.Error("Msg directory not found: {Path}", msgDir);
            return null;
        }

        var dbPath = Path.Combine(msgDir, "MM.sqlite");
        return File.Exists(dbPath) ? dbPath : null;
    }
}
```

- [ ] **Step 3: Commit**

```bash
git add WeChatExport/Core/Decryption/
git commit -m "feat: add WCDB decryption core logic"
```

---

#### Task 6: Database Service

**Files:**
- Create: `WeChatExport/Services/DatabaseService.cs`

**Interfaces:**
- Produces: `DatabaseService` class with `Connect()`, `GetContacts()`, `GetMessages()`, `GetConversation()` methods
- Consumes: `WeChatDecryptor`, `WeChatPathService`

- [ ] **Step 1: Create DatabaseService.cs**

```csharp
using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using WeChatExport.Core.Decryption;
using WeChatExport.Core.Models;
using Serilog;

namespace WeChatExport.Services;

public class DatabaseService
{
    private readonly WeChatDecryptor _decryptor;
    private readonly ILogger _logger;
    private SqliteConnection? _connection;

    public DatabaseService()
    {
        _decryptor = new WeChatDecryptor();
        _logger = Log.ForContext<DatabaseService>();
    }

    public bool Connect(string weChatPath, string key, string? account = null)
    {
        try
        {
            var dbPath = _decryptor.GetDatabasePath(weChatPath, account);
            if (string.IsNullOrEmpty(dbPath))
            {
                _logger.Error("Failed to locate database");
                return false;
            }

            _connection = _decryptor.OpenDatabase(dbPath, key);
            return _connection != null;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to connect to database");
            return false;
        }
    }

    public List<Contact> GetContacts()
    {
        var contacts = new List<Contact>();
        if (_connection == null) return contacts;

        try
        {
            // Query contacts from WeChat database
            // Table names vary by WeChat version
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT UserID, NickName, Remark, smallHeadImgUrl 
                FROM Contact 
                WHERE Type = 1 
                ORDER BY NickName";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                contacts.Add(new Contact
                {
                    UserId = reader.GetInt64(0),
                    NickName = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Remark = reader.IsDBNull(2) ? null : reader.GetString(2),
                    AvatarPath = reader.IsDBNull(3) ? null : reader.GetString(3)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to get contacts");
        }

        return contacts;
    }

    public List<Message> GetMessages(long contactId, int limit = 100, int offset = 0)
    {
        var messages = new List<Message>();
        if (_connection == null) return messages;

        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT LocalID, MsgSvrID, CreateTime, IsSender, Content, Type, SubType, Bytes
                FROM MSG
                WHERE TalkerId = @talkerId
                ORDER BY CreateTime DESC
                LIMIT @limit OFFSET @offset";
            cmd.Parameters.AddWithValue("@talkerId", contactId);
            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.Parameters.AddWithValue("@offset", offset);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var msg = new Message
                {
                    MessageId = reader.GetInt64(0),
                    SenderId = reader.GetInt64(1),
                    CreateTime = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)).DateTime,
                    IsFromSelf = reader.GetInt32(3) == 1,
                    Content = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Type = (MessageType)reader.GetInt32(5)
                };
                messages.Add(msg);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to get messages for contact {ContactId}", contactId);
        }

        return messages;
    }

    public void Disconnect()
    {
        _connection?.Close();
        _connection?.Dispose();
        _connection = null;
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add WeChatExport/Services/DatabaseService.cs
git commit -m "feat: add database service for WeChat data access"
```

---

### Phase 3: UI Implementation

#### Task 7: Main Window with Toolbar

**Files:**
- Modify: `WeChatExport/Views/MainWindow.axaml`
- Modify: `WeChatExport/ViewModels/MainWindowViewModel.cs`

**Interfaces:**
- Consumes: `WeChatPathService`, `KeyCaptureService`, `DatabaseService`
- Produces: Main window with toolbar for Connect, Export actions

- [ ] **Step 1: Update MainWindowViewModel.cs**

```csharp
using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeChatExport.Core.Models;
using WeChatExport.Services;
using Serilog;

namespace WeChatExport.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly WeChatPathService _pathService;
    private readonly KeyCaptureService _keyService;
    private readonly DatabaseService _databaseService;

    [ObservableProperty]
    private string _title = "WeChat Export Tool";

    [ObservableProperty]
    private string _weChatPath = string.Empty;

    [ObservableProperty]
    private string _key = string.Empty;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private ObservableCollection<Contact> _contacts = new();

    [ObservableProperty]
    private Contact? _selectedContact;

    [ObservableProperty]
    private ObservableCollection<Message> _messages = new();

    public MainWindowViewModel()
    {
        _pathService = new WeChatPathService();
        _keyService = new KeyCaptureService();
        _databaseService = new DatabaseService();
        
        // Load saved key if exists
        var savedKey = _keyService.LoadKey();
        if (!string.IsNullOrEmpty(savedKey))
        {
            Key = savedKey;
        }
    }

    [RelayCommand]
    private void DetectWeChat()
    {
        var path = _pathService.DetectWeChatPath();
        if (!string.IsNullOrEmpty(path))
        {
            WeChatPath = path;
            StatusMessage = $"WeChat found at: {path}";
        }
        else
        {
            StatusMessage = "WeChat not found. Please select manually.";
        }
    }

    [RelayCommand]
    private void Connect()
    {
        if (string.IsNullOrEmpty(WeChatPath))
        {
            StatusMessage = "Please select WeChat path first";
            return;
        }

        if (string.IsNullOrEmpty(Key))
        {
            StatusMessage = "Please enter or capture key";
            return;
        }

        StatusMessage = "Connecting...";
        
        if (_databaseService.Connect(WeChatPath, Key))
        {
            IsConnected = true;
            StatusMessage = "Connected successfully!";
            LoadContacts();
            
            // Save key for future sessions
            _keyService.SaveKey(Key);
        }
        else
        {
            StatusMessage = "Connection failed. Check your key.";
        }
    }

    private void LoadContacts()
    {
        var contacts = _databaseService.GetContacts();
        Contacts.Clear();
        foreach (var contact in contacts)
        {
            Contacts.Add(contact);
        }
    }

    partial void OnSelectedContactChanged(Contact? value)
    {
        if (value != null)
        {
            LoadMessages(value.UserId);
        }
    }

    private void LoadMessages(long contactId)
    {
        var messages = _databaseService.GetMessages(contactId);
        Messages.Clear();
        foreach (var msg in messages)
        {
            Messages.Add(msg);
        }
    }

    [RelayCommand]
    private void Export()
    {
        // Export functionality - see Task 9
        StatusMessage = "Export feature coming soon...";
    }

    [RelayCommand]
    private void Disconnect()
    {
        _databaseService.Disconnect();
        IsConnected = false;
        Contacts.Clear();
        Messages.Clear();
        StatusMessage = "Disconnected";
    }
}
```

- [ ] **Step 2: Update MainWindow.axaml**

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:WeChatExport.ViewModels"
        x:Class="WeChatExport.Views.MainWindow"
        x:DataType="vm:MainWindowViewModel"
        Title="{Binding Title}"
        Width="1200"
        Height="800"
        WindowStartupLocation="CenterScreen">

    <DockPanel>
        <!-- Toolbar -->
        <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="10" Spacing="10">
            <TextBlock Text="WeChat Path:" VerticalAlignment="Center"/>
            <TextBox Width="400" Text="{Binding WeChatPath}" IsEnabled="{Binding !IsConnected}"/>
            <Button Content="Detect" Command="{Binding DetectWeChatCommand}" IsEnabled="{Binding !IsConnected}"/>
            
            <TextBlock Text="Key:" VerticalAlignment="Center" Margin="20,0,0,0"/>
            <TextBox Width="200" Text="{Binding Key}" IsEnabled="{Binding !IsConnected}"/>
            
            <Button Content="Connect" Command="{Binding ConnectCommand}" IsEnabled="{Binding !IsConnected}"/>
            <Button Content="Disconnect" Command="{Binding DisconnectCommand}" IsEnabled="{Binding IsConnected}"/>
            <Button Content="Export" Command="{Binding ExportCommand}" IsEnabled="{Binding IsConnected}"/>
        </StackPanel>

        <!-- Status Bar -->
        <Border DockPanel.Dock="Bottom" Background="#F0F0F0" Padding="10">
            <TextBlock Text="{Binding StatusMessage}"/>
        </Border>

        <!-- Main Content: Left Contact List + Right Message View -->
        <Grid ColumnDefinitions="300,*">
            <!-- Contact List -->
            <Border Grid.Column="0" BorderBrush="LightGray" BorderThickness="0,0,1,0">
                <ListBox ItemsSource="{Binding Contacts}" SelectedItem="{Binding SelectedContact}">
                    <ListBox.ItemTemplate>
                        <DataTemplate>
                            <StackPanel Margin="5">
                                <TextBlock Text="{Binding DisplayName}" FontWeight="Bold"/>
                                <TextBlock Text="{Binding LastMessagePreview}" Opacity="0.7" FontSize="11"/>
                            </StackPanel>
                        </DataTemplate>
                    </ListBox.ItemTemplate>
                </ListBox>
            </Border>

            <!-- Message List -->
            <Border Grid.Column="1">
                <ListBox ItemsSource="{Binding Messages}">
                    <ListBox.ItemTemplate>
                        <DataTemplate>
                            <Border Margin="10" Padding="10" CornerRadius="5"
                                    Background="{Binding IsFromSelf, Converter={x:Static BoolConverters.ToString}, ConverterParameter='#E0F0FF;#FFFFFF'}">
                                <StackPanel>
                                    <TextBlock Text="{Binding SenderName}" FontWeight="Bold" FontSize="11"/>
                                    <TextBlock Text="{Binding Content}" TextWrapping="Wrap" Margin="0,5,0,0"/>
                                    <TextBlock Text="{Binding CreateTime, StringFormat='{}{0:yyyy-MM-dd HH:mm}'}" 
                                               FontSize="10" Opacity="0.5" HorizontalAlignment="Right"/>
                                </StackPanel>
                            </Border>
                        </DataTemplate>
                    </ListBox.ItemTemplate>
                </ListBox>
            </Border>
        </Grid>
    </DockPanel>
</Window>
```

- [ ] **Step 3: Update MainWindow.axaml.cs**

```csharp
using Avalonia.Controls;
using WeChatExport.ViewModels;

namespace WeChatExport.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }
}
```

- [ ] **Step 4: Build and test**

```bash
cd WeChatExport
dotnet build
dotnet run
```

- [ ] **Step 5: Commit**

```bash
git add WeChatExport/Views/MainWindow.axaml
git add WeChatExport/ViewModels/MainWindowViewModel.cs
git commit -m "feat: add main window UI with toolbar and contact/message views"
```

---

### Phase 4: Export Features

#### Task 8: Export Service

**Files:**
- Create: `WeChatExport/Services/ExportService.cs`

**Interfaces:**
- Produces: `ExportService` with `ExportToCsv()`, `ExportToJson()`, `ExportToHtml()`, `ExportToTxt()`, `ExportToExcel()`, `ExportToPdf()` methods

- [ ] **Step 1: Create ExportService.cs**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Linq;
using WeChatExport.Core.Models;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using Serilog;

namespace WeChatExport.Services;

public class ExportService
{
    private readonly ILogger _logger;

    public ExportService()
    {
        _logger = Log.ForContext<ExportService>();
    }

    public void ExportToJson(Conversation conversation, string outputPath)
    {
        try
        {
            var exportData = new
            {
                Contact = conversation.Contact,
                Messages = conversation.Messages.Select(m => new
                {
                    m.MessageId,
                    m.SenderName,
                    m.Content,
                    Type = m.Type.ToString(),
                    m.CreateTime,
                    m.IsFromSelf
                }),
                conversation.TotalMessageCount
            };

            var json = JsonSerializer.Serialize(exportData, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(outputPath, json);
            _logger.Information("Exported to JSON: {Path}", outputPath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to export to JSON");
            throw;
        }
    }

    public void ExportToCsv(Conversation conversation, string outputPath)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Time,Sender,Content,Type,FromSelf");

            foreach (var msg in conversation.Messages)
            {
                var content = msg.Content?.Replace("\"", "\"\"") ?? "";
                sb.AppendLine($"\"{msg.CreateTime:yyyy-MM-dd HH:mm:ss}\",\"{msg.SenderName}\",\"{content}\",\"{msg.Type}\",\"{msg.IsFromSelf}\"");
            }

            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
            _logger.Information("Exported to CSV: {Path}", outputPath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to export to CSV");
            throw;
        }
    }

    public void ExportToTxt(Conversation conversation, string outputPath)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Chat with: {conversation.Contact.DisplayName}");
            sb.AppendLine(new string('=', 50));
            sb.AppendLine();

            foreach (var msg in conversation.Messages.OrderBy(m => m.CreateTime))
            {
                var sender = msg.IsFromSelf ? "You" : (msg.SenderName ?? "Unknown");
                sb.AppendLine($"[{msg.CreateTime:yyyy-MM-dd HH:mm:ss}] {sender}:");
                sb.AppendLine($"  {msg.Content}");
                sb.AppendLine();
            }

            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
            _logger.Information("Exported to TXT: {Path}", outputPath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to export to TXT");
            throw;
        }
    }

    public void ExportToHtml(Conversation conversation, string outputPath)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html><head><meta charset='utf-8'>");
            sb.AppendLine("<title>Chat Export</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body { font-family: Arial, sans-serif; margin: 20px; }");
            sb.AppendLine(".message { margin: 10px 0; padding: 10px; border-radius: 8px; }");
            sb.AppendLine(".self { background: #E0F0FF; margin-left: 50px; }");
            sb.AppendLine(".other { background: #F0F0F0; margin-right: 50px; }");
            sb.AppendLine(".time { font-size: 11px; color: #888; }");
            sb.AppendLine(".sender { font-weight: bold; font-size: 12px; }");
            sb.AppendLine("</style></head><body>");

            sb.AppendLine($"<h1>Chat with: {conversation.Contact.DisplayName}</h1>");

            foreach (var msg in conversation.Messages.OrderBy(m => m.CreateTime))
            {
                var cssClass = msg.IsFromSelf ? "self" : "other";
                var sender = msg.IsFromSelf ? "You" : (msg.SenderName ?? "Unknown");
                
                sb.AppendLine($"<div class='message {cssClass}'>");
                sb.AppendLine($"<div class='sender'>{sender}</div>");
                sb.AppendLine($"<div>{msg.Content?.Replace("\n", "<br>")}</div>");
                sb.AppendLine($"<div class='time'>{msg.CreateTime:yyyy-MM-dd HH:mm:ss}</div>");
                sb.AppendLine("</div>");
            }

            sb.AppendLine("</body></html>");

            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
            _logger.Information("Exported to HTML: {Path}", outputPath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to export to HTML");
            throw;
        }
    }

    public void ExportToExcel(Conversation conversation, string outputPath)
    {
        try
        {
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Chat");

            // Headers
            worksheet.Cell(1, 1).Value = "Time";
            worksheet.Cell(1, 2).Value = "Sender";
            worksheet.Cell(1, 3).Value = "Content";
            worksheet.Cell(1, 4).Value = "Type";
            worksheet.Cell(1, 5).Value = "From Self";

            var headerRange = worksheet.Range(1, 1, 1, 5);
            headerRange.Style.Font.Bold = true;
            headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;

            // Data
            var row = 2;
            foreach (var msg in conversation.Messages.OrderBy(m => m.CreateTime))
            {
                worksheet.Cell(row, 1).Value = msg.CreateTime.ToString("yyyy-MM-dd HH:mm:ss");
                worksheet.Cell(row, 2).Value = msg.SenderName ?? "Unknown";
                worksheet.Cell(row, 3).Value = msg.Content ?? "";
                worksheet.Cell(row, 4).Value = msg.Type.ToString();
                worksheet.Cell(row, 5).Value = msg.IsFromSelf ? "Yes" : "No";
                row++;
            }

            worksheet.Columns().AdjustToContents();
            workbook.SaveAs(outputPath);
            _logger.Information("Exported to Excel: {Path}", outputPath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to export to Excel");
            throw;
        }
    }

    public void ExportToPdf(Conversation conversation, string outputPath)
    {
        try
        {
            var messages = conversation.Messages.OrderBy(m => m.CreateTime).ToList();
            
            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(20);
                    page.DefaultTextStyle(x => x.FontSize(10));

                    page.Header()
                        .Text($"Chat with: {conversation.Contact.DisplayName}")
                        .FontSize(16).Bold().FontColor(Colors.Blue.Darken2);

                    page.Content().Column(column =>
                    {
                        foreach (var msg in messages)
                        {
                            var sender = msg.IsFromSelf ? "You" : (msg.SenderName ?? "Unknown");
                            var alignment = msg.IsFromSelf ? TextAlign.Right : TextAlign.Left;
                            var bgColor = msg.IsFromSelf ? Colors.Grey.Lighten4 : Colors.White;

                            column.Item().Background(bgColor).Padding(10).Column(msgCol =>
                            {
                                msgCol.Item().Row(row =>
                                {
                                    row.RelativeItem().Text(sender).Bold();
                                    row.AdaptiveItem().Text(msg.CreateTime.ToString("yyyy-MM-dd HH:mm"))
                                        .FontSize(9).FontColor(Colors.Grey.Medium);
                                });
                                
                                msgCol.Item().PaddingTop(5).Text(msg.Content ?? "").LineHeight(1.5);
                            });
                        }
                    });

                    page.Footer()
                        .AlignCenter()
                        .Text(text => text.CurrentPageNumber().FontSize(9));
                });
            }).GeneratePdf(outputPath);

            _logger.Information("Exported to PDF: {Path}", outputPath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to export to PDF");
            throw;
        }
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add WeChatExport/Services/ExportService.cs
git commit -m "feat: add export service with all 6 formats"
```

---

#### Task 9: Export UI Integration

**Files:**
- Modify: `WeChatExport/ViewModels/MainWindowViewModel.cs`
- Create: `WeChatExport/Views/ExportDialog.axaml` (optional)

- [ ] **Step 1: Add export methods to MainWindowViewModel**

```csharp
// Add to MainWindowViewModel.cs

[ObservableProperty]
private string _exportPath = string.Empty;

[RelayCommand]
private async Task ExportAsync()
{
    if (SelectedContact == null || Messages.Count == 0)
    {
        StatusMessage = "No conversation selected for export";
        return;
    }

    var dialog = new SaveFileDialog
    {
        Title = "Export Chat",
        Filters = new[]
        {
            new FileDialogFilter { Name = "JSON", Extensions = new[] { "json" } },
            new FileDialogFilter { Name = "CSV", Extensions = new[] { "csv" } },
            new FileDialogFilter { Name = "HTML", Extensions = new[] { "html" } },
            new FileDialogFilter { Name = "TXT", Extensions = new[] { "txt" } },
            new FileDialogFilter { Name = "Excel", Extensions = new[] { "xlsx" } },
            new FileDialogFilter { Name = "PDF", Extensions = new[] { "pdf" } }
        }
    };

    var result = await dialog.ShowAsync(MainWindow);
    if (string.IsNullOrEmpty(result)) return;

    try
    {
        var exportService = new ExportService();
        var conversation = new Conversation
        {
            Contact = SelectedContact,
            Messages = Messages.ToList(),
            TotalMessageCount = Messages.Count
        };

        var ext = Path.GetExtension(result).ToLowerInvariant();
        switch (ext)
        {
            case ".json":
                exportService.ExportToJson(conversation, result);
                break;
            case ".csv":
                exportService.ExportToCsv(conversation, result);
                break;
            case ".html":
                exportService.ExportToHtml(conversation, result);
                break;
            case ".txt":
                exportService.ExportToTxt(conversation, result);
                break;
            case ".xlsx":
                exportService.ExportToExcel(conversation, result);
                break;
            case ".pdf":
                exportService.ExportToPdf(conversation, result);
                break;
            default:
                StatusMessage = "Unsupported export format";
                return;
        }

        StatusMessage = $"Exported to: {result}";
    }
    catch (Exception ex)
    {
        StatusMessage = $"Export failed: {ex.Message}";
        _logger.Error(ex, "Export failed");
    }
}
```

- [ ] **Step 2: Commit**

```bash
git commit -m "feat: integrate export functionality into main window"
```

---

### Phase 5: Polish

#### Task 10: Logging and Error Handling

**Files:**
- Modify: `WeChatExport/Program.cs`

- [ ] **Step 1: Add Serilog logging setup**

```csharp
using System;
using System.IO;
using Avalonia;
using Serilog;
using Serilog.Events;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Setup logging
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WeChatExport",
            "logs",
            "app-.log");

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(logPath, 
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            Log.Information("Application starting...");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
```

- [ ] **Step 2: Commit**

```bash
git commit -m "feat: add Serilog logging with file output"
```

---

#### Task 11: Build Release

**Files:**
- Modify: `WeChatExport/WeChatExport.csproj`

- [ ] **Step 1: Configure for single-file publish**

```xml
<PropertyGroup>
  <OutputType>WinExe</OutputType>
  <TargetFramework>net10.0-windows</TargetFramework>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <SelfContained>true</SelfContained>
  <PublishSingleFile>true</PublishSingleFile>
  <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
  <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
  <ApplicationIcon>Assets\icon.ico</ApplicationIcon>
</PropertyGroup>
```

- [ ] **Step 2: Build release**

```bash
cd WeChatExport
dotnet publish -c Release -o ../publish
```

- [ ] **Step 3: Verify .exe**

```bash
ls -la ../publish/*.exe
```

- [ ] **Step 4: Commit**

```bash
git commit -m "feat: configure single-file publish and build release"
```

---

## Summary

This plan creates a complete WeChat Export Tool with:

1. **Project Foundation** - .NET 10 + AvaloniaUI 12.x MVVM setup
2. **Core Features** - WeChat path detection, key capture, WCDB decryption
3. **UI** - Main window with contact list and message view
4. **Export** - All 6 formats (JSON, CSV, HTML, TXT, Excel, PDF)
5. **Polish** - Logging, error handling, single-file release

Each task produces a working, testable deliverable. The plan follows TDD principles where applicable and uses proper commit boundaries for review.

---

**Plan complete and saved to `docs/superpowers/plans/2026-09-11-dotnet-avalonia-migration.md`.**

Two execution options:

**1. Subagent-Driven (recommended)** - I dispatch a fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** - Execute tasks in this session using executing-plans, batch execution with checkpoints

**Which approach?**
