# WeChat Export Tool - .NET 10 + AvaloniaUI Migration Design

## Overview

Migrate the existing Python-based WeChat Export Tool to .NET 10 with AvaloniaUI 12.x for cross-platform desktop UI.

## Technology Stack

| Component | Technology | Version |
|-----------|------------|---------|
| Runtime | .NET SDK | 10.0.x |
| UI Framework | AvaloniaUI | 12.x |
| Database | Microsoft.Data.Sqlite | 9.x |
| Excel Export | ClosedXML | 0.104.x |
| PDF Export | QuestPDF | 2026.x |
| Logging | Serilog | 4.x |

## Project Structure

Single project: `WeChatExport.csproj`

```
WeChatExport/
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
│   │   └── CryptoUtils.cs        # Crypto utilities
│   └── Models/
│       ├── Contact.cs            # Contact model
│       ├── Message.cs           # Message model
│       └── Conversation.cs      # Conversation model
└── Assets/
    └── icon.ico                  # Application icon
```

## Core Features

### 1. WeChat Path Detection
- Scan common WeChat installation paths
- Check registry for installation path
- Allow manual path selection

### 2. Key Capture
- Detect WeChat running process
- Inject DLL or use memory reading to capture key
- Support manual key entry
- Persist key for future sessions

### 3. Database Connection
- Connect to WeChat's MM.sqlite
- Decrypt database using captured key
- Query contacts and messages

### 4. Contact Browsing
- List all contacts/conversations
- Search/filter contacts
- Show last message preview
- Show unread count

### 5. Message Viewing
- Display message history
- Support text, image, emoji, video, voice
- Lazy loading for large histories
- Search within conversation

### 6. Export Formats
| Format | Library | Notes |
|--------|---------|-------|
| PDF | QuestPDF | Current implementation |
| CSV | Built-in | System.IO |
| Excel | ClosedXML | Full formatting |
| HTML | Built-in | Custom template |
| TXT | Built-in | Plain text |
| JSON | System.Text.Json | Structured data |

## UI/UX Design

### Layout
- **Single window** with tabbed navigation
- **Left panel**: Contact list
- **Right panel**: Message view
- **Top toolbar**: Actions (Connect, Export, Settings)

### Color Scheme
- Follow Windows WeChat color scheme
- Light theme default

### Interactions
- Double-click contact to open conversation
- Right-click for context menu (export, delete)
- Drag-and-drop for file operations

## Data Flow

```
User Input → ViewModel → Service → Core Logic → Database
                ↓
            View Update
```

## Key Implementation Details

### WCDB Decryption
The WeChat database (MM.sqlite) uses WCDB with SQLCipher. The decryption requires:
1. Find the key from WeChat process memory
2. Derive the actual encryption key
3. Open SQLite database with the key

### Message Parsing
Messages are stored in binary format in the database:
- Text: Direct UTF-8 string
- Images: Reference to media files
- Complex types: Need specialized parsing

### Export Pipeline
1. Load messages from database
2. Transform to export format
3. Apply formatting (Excel: styles, PDF: layout)
4. Write to output file
5. Show completion notification

## Migration from Python

| Python Component | .NET Equivalent |
|------------------|-----------------|
| Python 3.13 | C# / .NET 10 |
| tkinter | AvaloniaUI 12.x |
| electron (WCDB) | C# reimplementation |
| pandas (Excel) | ClosedXML |
| reportlab (PDF) | QuestPDF |
| requests | HttpClient |

## Testing Strategy

1. **Unit Tests** - Core decryption, export logic
2. **Integration Tests** - Database connection, export
3. **UI Tests** - Avalonia UI tests

## Build & Release

- Single executable via `dotnet publish`
- Self-contained deployment
- Windows x64 target

## Timeline

Phase 1: Project Setup (Foundation)
- Initialize .NET 10 project
- Add AvaloniaUI
- Basic UI shell

Phase 2: Core Features
- WeChat path detection
- Key capture
- Database connection

Phase 3: UI Implementation
- Contact list view
- Message view
- Navigation

Phase 4: Export Features
- Implement all export formats
- UI for export options

Phase 5: Polish
- Error handling
- Logging
- Testing
- Build release

## Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| WCDB decryption complexity | High | Research existing implementations, start with known key format |
| Message type parsing | Medium | Implement incrementally, test with sample data |
| Avalonia 12.x compatibility | Low | Use stable preview, check release notes |

## Success Criteria

- [ ] Application launches without errors
- [ ] Can detect WeChat installation
- [ ] Can capture/decrypt database key
- [ ] Can display contact list
- [ ] Can display message history
- [ ] Can export to all 6 formats
- [ ] Build produces single .exe file
