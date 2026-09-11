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
    private Contact? _selectedContact;

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

        // Initialize with default WeChat path
        TrySetDefaultWeChatPath();
    }

    private void TrySetDefaultWeChatPath()
    {
        try
        {
            var defaultPath = _weChatPathService.DetectWeChatPath();
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
    private void BrowseWeChatPath()
    {
        try
        {
            var defaultPath = _weChatPathService.DetectWeChatPath();
            if (!string.IsNullOrEmpty(defaultPath) && Directory.Exists(defaultPath))
            {
                WeChatPath = defaultPath;
                StatusMessage = $"WeChat path set to: {WeChatPath}";
            }
            else
            {
                StatusMessage = "Could not auto-detect WeChat installation";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to browse WeChat path");
            StatusMessage = "Failed to browse WeChat path";
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task Connect()
    {
        IsLoading = true;
        StatusMessage = "Connecting to database...";

        try
        {
            await Task.Run(() =>
            {
                // First try to find the MSG database
                var msgDbPath = FindMsgDatabase();

                if (string.IsNullOrEmpty(msgDbPath))
                {
                    // Try default location
                    msgDbPath = _databaseService.GetDefaultMsgDatabasePath();
                }

                if (string.IsNullOrEmpty(msgDbPath))
                {
                    throw new Exception("Could not find WeChat MSG database");
                }

                // Apply decryption key if provided
                if (!string.IsNullOrEmpty(DecryptionKey))
                {
                    // Key would be used for decrypting the database
                    Log.Information("Using provided decryption key");
                }

                return _databaseService.Connect(msgDbPath);
            });

            if (_databaseService.IsConnected)
            {
                IsConnected = true;
                StatusMessage = "Connected successfully";

                // Load contacts
                await LoadContacts();
            }
            else
            {
                StatusMessage = "Failed to connect to database";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to connect to database");
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
        if (string.IsNullOrEmpty(WeChatPath))
            return null;

        // Look for MSG database in the WeChat installation path
        // Typically: %APPDATA%\Tencent\WeChat\Msg\Msg*.db
        var msgPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Tencent", "WeChat", "Msg");

        if (Directory.Exists(msgPath))
        {
            var dbFiles = Directory.GetFiles(msgPath, "Msg*.db");
            if (dbFiles.Length > 0)
            {
                // Return the most recent one
                return dbFiles.OrderByDescending(f => File.GetLastWriteTime(f)).First();
            }
        }

        return null;
    }

    [RelayCommand]
    private async Task LoadContacts()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Loading contacts...";

            var contacts = await Task.Run(() => _databaseService.GetContacts());

            Contacts.Clear();
            foreach (var contact in contacts)
            {
                Contacts.Add(contact);
            }

            ContactCount = Contacts.Count;
            StatusMessage = $"Loaded {ContactCount} contacts";
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

            var messages = await Task.Run(() =>
                _databaseService.GetMessages(SelectedContact.UserId, 1000));

            Messages.Clear();
            foreach (var message in messages)
            {
                Messages.Add(message);
            }

            MessageCount = Messages.Count;
            StatusMessage = $"Loaded {MessageCount} messages";
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

    [RelayCommand]
    private void Refresh()
    {
        if (IsConnected)
        {
            LoadContactsCommand.Execute(null);
        }
    }

    public void Cleanup()
    {
        _databaseService.Dispose();
    }
}
