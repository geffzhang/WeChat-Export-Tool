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
