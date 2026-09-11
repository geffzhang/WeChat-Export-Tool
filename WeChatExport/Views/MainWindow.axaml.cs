using Avalonia.Controls;
using WeChatExport.ViewModels;

namespace WeChatExport.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// The currently active main window. Used by the ViewModel to reach the
    /// TopLevel/IStorageProvider needed to show file pickers.
    /// </summary>
    public static Window? Current { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
        Current = this;
    }
}
