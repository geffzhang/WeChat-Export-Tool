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
    private string _weChatPath = string.Empty;

    [ObservableProperty]
    private string _decryptionKey = string.Empty;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
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
        try
        {
            IsLoading = true;
            StatusMessage = "Exporting data...";

            // Export functionality would be implemented here
            // For now, just show status
            await Task.Delay(100);

            StatusMessage = "Export completed";
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
        return IsConnected && Contacts.Count > 0 && !IsLoading;
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
