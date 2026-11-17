using System.Windows;
using ProbeDesktop.App.ViewModels;
using ProbeDesktop.App.Views;

namespace ProbeDesktop.App.Windows;

/// <summary>模型鉴别记录的非模态独立窗口。复用 HistoryView 与 HistoryViewModel，但创建新的视图实例，避免与主界面的逻辑父级冲突。</summary>
public partial class ProbeHistoryWindow : Window
{
    public ProbeHistoryWindow(HistoryViewModel vm)
    {
        InitializeComponent();
        Host.Children.Add(new HistoryView { DataContext = vm });
    }
}
