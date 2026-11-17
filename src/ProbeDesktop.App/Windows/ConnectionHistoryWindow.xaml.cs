using System.Windows;
using ProbeDesktop.App.ViewModels;

namespace ProbeDesktop.App.Windows;

/// <summary>连接记录的非模态独立窗口。独立于主窗口：主窗口隐藏或最小化不影响它，只在退出时由 MainWindow 关闭。</summary>
public partial class ConnectionHistoryWindow : Window
{
    public ConnectionHistoryWindow(ConnectionHistoryViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) => await vm.ReloadAsync();
    }
}
