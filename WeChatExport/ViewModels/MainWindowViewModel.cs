using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using WeChatExport.Core.Models;
using WeChatExport.Services;

namespace WeChatExport.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly DatabaseService _databaseService;
    private readonly WeChatPathService _weChatPathService;
    private readonly KeyCaptureService _keyCaptureService;

    [ObservableProperty]
    private string _title = "WeChat Export Tool";

    // Toolbar properties
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _weChatPath = string.Empty;

    [ObservableProperty]
    private string _decryptionKey = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMessagesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyPropertyChangedFor(nameof(ShowConnectHint))]
    [NotifyPropertyChangedFor(nameof(ShowSelectContactHint))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadMessagesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private bool _isLoading;

    // Contact list
    [ObservableProperty]
    private ObservableCollection<Contact> _contacts = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSelectContactHint))]
    private Contact? _selectedContact;

    /// <summary>
    /// Empty-state hint shown when there is no database connection yet. The
    /// connection-oriented wording lives here so the view does not have to invert
    /// its own visibility rule (which previously showed the "select a contact"
    /// text while disconnected).
    /// </summary>
    public bool ShowConnectHint => !IsConnected;

    /// <summary>Empty-state hint shown once connected but with no conversation open.</summary>
    public bool ShowSelectContactHint => IsConnected && SelectedContact is null;

    // Message view
    [ObservableProperty]
    private ObservableCollection<Message> _messages = new();

    [ObservableProperty]
    private Message? _selectedMessage;

    // Status bar
    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private int _contactCount;

    [ObservableProperty]
    private int _messageCount;

    public MainWindowViewModel()
    {
        _databaseService = new DatabaseService();
        _weChatPathService = new WeChatPathService();
        _keyCaptureService = new KeyCaptureService();

        // ObservableCollection mutations do not raise PropertyChanged, so the
        // attribute-based NotifyCanExecuteChangedFor cannot see them. CanExport()
        // depends on Messages.Count (and, defensively, Contacts.Count), so we must
        // re-notify the commands whenever either collection changes.
        Messages.CollectionChanged += (_, _) =>
        {
            ExportCommand.NotifyCanExecuteChanged();
            LoadMessagesCommand.NotifyCanExecuteChanged();
        };
        Contacts.CollectionChanged += (_, _) =>
        {
            ExportCommand.NotifyCanExecuteChanged();
        };

        RestoreSavedKey();

        // Initialize with default WeChat path
        TrySetDefaultWeChatPath();
    }

    /// <summary>
    /// Prefills the decryption key from the last successful session. Never
    /// overwrites a value the user has already typed.
    /// </summary>
    private void RestoreSavedKey()
    {
        try
        {
            if (!string.IsNullOrEmpty(DecryptionKey))
                return;

            var savedKey = _keyCaptureService.LoadKey();
            if (!string.IsNullOrEmpty(savedKey))
            {
                DecryptionKey = savedKey;
                Log.Information("Restored previously saved decryption key");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to restore saved decryption key");
        }
    }

    private void TrySetDefaultWeChatPath()
    {
        try
        {
            // Prefer a real data root (where the message databases actually live).
            // The install directory is only a last resort so the field is not left
            // empty on a machine where detection of the data folder fails.
            var defaultPath = _weChatPathService.DetectWeChatDataRoot()
                              ?? _weChatPathService.DetectWeChatPath();

            if (!string.IsNullOrEmpty(defaultPath) && Directory.Exists(defaultPath))
            {
                WeChatPath = defaultPath;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to get default WeChat path");
        }
    }

    partial void OnSelectedContactChanged(Contact? value)
    {
        if (value != null)
        {
            LoadMessagesCommand.Execute(null);
        }
        else
        {
            Messages.Clear();
            MessageCount = 0;
        }

        // Both commands gate on SelectedContact, which is not an
        // ObservableProperty-driven command dependency for attributes to catch here
        // (the property itself is, but the *commands* are driven by this handler).
        LoadMessagesCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task BrowseWeChatPath()
    {
        var storageProvider = Views.MainWindow.Current?.StorageProvider;
        if (storageProvider == null)
        {
            StatusMessage = "Browse unavailable: no active window";
            return;
        }

        try
        {
            // A real folder picker. This previously re-ran auto-detection, which
            // silently overwrote whatever the user had typed into the field.
            var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select the WeChat data folder (e.g. Documents\\WeChat Files or an account folder)",
                AllowMultiple = false
            });

            var picked = folders.Count > 0 ? folders[0].Path.LocalPath : null;
            if (string.IsNullOrEmpty(picked))
            {
                StatusMessage = "Folder selection cancelled";
                return;
            }

            WeChatPath = picked;
            StatusMessage = $"WeChat data folder set to: {picked}";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to browse for the WeChat data folder");
            StatusMessage = "Failed to open the folder picker";
        }
    }

    /// <summary>
    /// Manual key-capture attempt. A single process enumeration answers both "is it
    /// running?" and "did we get a key?", so pressing the button no longer walks the
    /// process list twice. The process-memory scan itself remains a deliberate stub
    /// (see KeyCaptureService.AttemptCapture).
    /// </summary>
    [RelayCommand]
    private void CaptureKey()
    {
        var attempt = _keyCaptureService.AttemptCapture();

        if (!string.IsNullOrWhiteSpace(attempt.Key))
        {
            DecryptionKey = attempt.Key;
            StatusMessage = "Decryption key captured from the running WeChat process";
            return;
        }

        StatusMessage = attempt.WeChatRunning
            ? "WeChat is running, but automatic key capture is not implemented yet - paste the key manually."
            : "WeChat does not appear to be running. Start and sign in to WeChat, or paste the key manually.";
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task Connect()
    {
        IsLoading = true;
        StatusMessage = "Connecting to database...";

        try
        {
            // Capture the key on the UI thread and hand it to the database layer.
            // It previously stopped here: it was logged and then dropped on the
            // floor, so the connection was always attempted without it.
            var key = DecryptionKey;

            var result = await Task.Run(() =>
            {
                var msgDbPath = FindMsgDatabase();

                if (string.IsNullOrEmpty(msgDbPath))
                {
                    return new ConnectResult(
                        ConnectOutcome.FileNotFound,
                        "Could not find a WeChat message database. Check the WeChat data folder and try again.");
                }

                Log.Information("Connecting to {DbPath} (key supplied: {HasKey})", msgDbPath, !string.IsNullOrEmpty(key));
                return _databaseService.Connect(msgDbPath, key);
            });

            if (result.IsSuccess)
            {
                IsConnected = true;
                StatusMessage = result.Message;

                // Persist the key that actually worked, so the next session does not
                // have to re-enter it.
                if (!string.IsNullOrEmpty(key))
                {
                    _keyCaptureService.SaveKey(key);
                }

                await LoadContacts();
                return;
            }

            // Each failure mode reports its own reason rather than leaving a green
            // "Connected" light over an empty contact list.
            Log.Warning("Connect failed ({Outcome}): {Message}", result.Outcome, result.Message);
            IsConnected = false;
            ContactCount = 0;
            MessageCount = 0;
            Contacts.Clear();
            Messages.Clear();
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to connect to database");
            IsConnected = false;
            StatusMessage = $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanConnect()
    {
        return !IsLoading && !string.IsNullOrEmpty(WeChatPath);
    }

    private string? FindMsgDatabase()
    {
        // WeChatPath now actually drives the lookup: it is searched first, and the
        // known default locations are only used as a fallback. The previous version
        // ignored the field entirely and always scanned
        // %APPDATA%\Tencent\WeChat\Msg, which is not where WeChat stores history.
        return _weChatPathService.FindMsgDatabase(WeChatPath);
    }

    [RelayCommand]
    private async Task LoadContacts()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Loading contacts...";

            var contacts = await Task.Run(() => _databaseService.GetContacts());
            var error = _databaseService.LastError;

            Contacts.Clear();
            foreach (var contact in contacts)
            {
                Contacts.Add(contact);
            }

            ContactCount = Contacts.Count;

            if (error is not null)
            {
                // A failed read must not masquerade as a successful empty result.
                Log.Error("Contact load reported an error: {Error}", error);
                StatusMessage = error;
            }
            else
            {
                StatusMessage = $"Loaded {ContactCount} contacts";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load contacts");
            StatusMessage = "Failed to load contacts";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadMessages))]
    private async Task LoadMessages()
    {
        if (SelectedContact == null)
            return;

        try
        {
            IsLoading = true;
            StatusMessage = $"Loading messages for {SelectedContact.DisplayName}...";

            var contact = SelectedContact;

            // Per-sender lookup so a message can be attributed to the sender that
            // actually wrote it. Keyed by the contact's RAW string identifier (a
            // wxid), because that is what the database stores in a message's Sender
            // column - keying by the numeric UserId could never match a wxid, and
            // CAST(Sender AS INTEGER) turns one into 0. Where a sender is not present
            // here, DatabaseService falls back to the conversation's contact name and
            // then to the raw identifier, which is what keeps one-to-one chats from
            // rendering a blank/discredited sender.
            var senderNames = Contacts
                .Where(c => !string.IsNullOrWhiteSpace(c.Identifier) && !string.IsNullOrWhiteSpace(c.DisplayName))
                .GroupBy(c => c.Identifier!)
                .ToDictionary(g => g.Key, g => g.First().DisplayName!);

            var messages = await Task.Run(() =>
                _databaseService.GetMessages(contact.UserId, 1000, contact.DisplayName, senderNames));
            var error = _databaseService.LastError;

            Messages.Clear();
            foreach (var message in messages)
            {
                Messages.Add(message);
            }

            MessageCount = Messages.Count;

            if (error is not null)
            {
                Log.Error("Message load reported an error: {Error}", error);
                StatusMessage = error;
            }
            else
            {
                StatusMessage = $"Loaded {MessageCount} messages";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load messages");
            StatusMessage = "Failed to load messages";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanLoadMessages()
    {
        return IsConnected && SelectedContact != null && !IsLoading;
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task Export()
    {
        if (SelectedContact == null)
        {
            StatusMessage = "Please select a contact first";
            return;
        }

        var storageProvider = Views.MainWindow.Current?.StorageProvider;
        if (storageProvider == null)
        {
            StatusMessage = "Export unavailable: no active window";
            return;
        }

        try
        {
            IsLoading = true;
            StatusMessage = "Choosing export location...";

            var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Chat",
                SuggestedFileName = $"chat_{SelectedContact.DisplayName}",
                DefaultExtension = "json",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("JSON")  { Patterns = new[] { "*.json" } },
                    new FilePickerFileType("CSV")   { Patterns = new[] { "*.csv" } },
                    new FilePickerFileType("Text")  { Patterns = new[] { "*.txt" } },
                    new FilePickerFileType("HTML")  { Patterns = new[] { "*.html" } },
                    new FilePickerFileType("Excel") { Patterns = new[] { "*.xlsx" } },
                    new FilePickerFileType("PDF")   { Patterns = new[] { "*.pdf" } },
                }
            });

            if (file == null)
            {
                StatusMessage = "Export cancelled";
                return;
            }

            var outputPath = file.Path.LocalPath;
            var exportService = new ExportService();
            var conversation = new Conversation
            {
                Contact = SelectedContact,
                Messages = Messages.ToList(),
                TotalMessageCount = Messages.Count
            };

            var ext = Path.GetExtension(outputPath).ToLowerInvariant();
            await Task.Run(() =>
            {
                switch (ext)
                {
                    case ".json": exportService.ExportToJson(conversation, outputPath); break;
                    case ".csv":  exportService.ExportToCsv(conversation, outputPath);  break;
                    case ".txt":  exportService.ExportToTxt(conversation, outputPath);  break;
                    case ".html": exportService.ExportToHtml(conversation, outputPath); break;
                    case ".xlsx": exportService.ExportToExcel(conversation, outputPath); break;
                    case ".pdf":  exportService.ExportToPdf(conversation, outputPath);  break;
                    default:
                        throw new NotSupportedException($"Unsupported export format: {ext}");
                }
            });

            StatusMessage = $"Exported to: {outputPath}";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to export data");
            StatusMessage = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanExport()
    {
        return IsConnected && SelectedContact != null && Messages.Count > 0 && !IsLoading;
    }

    [RelayCommand]
    private void Disconnect()
    {
        try
        {
            _databaseService.Disconnect();
            IsConnected = false;
            Contacts.Clear();
            Messages.Clear();
            ContactCount = 0;
            MessageCount = 0;
            SelectedContact = null;
            SelectedMessage = null;
            StatusMessage = "Disconnected";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to disconnect");
            StatusMessage = "Failed to disconnect";
        }
    }

    public void Cleanup()
    {
        _databaseService.Dispose();
    }
}
