using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace ProbeDesktop.App.Services;

/// <summary>
/// 通知区域图标（框架自带 WinForms NotifyIcon，无外部包、无外部素材）。
/// 只负责图标、菜单和事件；窗口显示/隐藏与退出确认由 MainWindow 处理。所有成员在 UI 线程使用。
/// </summary>
public sealed class TrayService : IDisposable, IProblemToastSink
{
    private WinForms.NotifyIcon? _icon;
    private WinForms.ContextMenuStrip? _menu;
    private Drawing.Icon? _image;
    private bool _disposed;

    /// <summary>图标已成功创建并可见。只有为 true 时才允许把窗口隐藏到托盘。</summary>
    public bool IsReady => _icon is { Visible: true } && !_disposed;

    public event EventHandler? OpenRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? ShowConnectionsRequested;
    public event EventHandler? ShowProbeHistoryRequested;

    /// <summary>创建图标。重复调用无副作用；失败返回 false，窗口则保持普通最小化到任务栏。</summary>
    public bool TryInitialize(string tooltip)
    {
        if (_disposed)
        {
            return false;
        }

        if (_icon is not null)
        {
            return IsReady;
        }

        try
        {
            _image = CreateIcon();
            _menu = new WinForms.ContextMenuStrip();
            var open = new WinForms.ToolStripMenuItem("打开主窗口");
            open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
            var connections = new WinForms.ToolStripMenuItem("连接巡检与记录");
            connections.Click += (_, _) => ShowConnectionsRequested?.Invoke(this, EventArgs.Empty);
            var probeHistory = new WinForms.ToolStripMenuItem("模型鉴别记录");
            probeHistory.Click += (_, _) => ShowProbeHistoryRequested?.Invoke(this, EventArgs.Empty);
            var exit = new WinForms.ToolStripMenuItem("退出");
            exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
            _menu.Items.Add(open);
            _menu.Items.Add(connections);
            _menu.Items.Add(probeHistory);
            _menu.Items.Add(new WinForms.ToolStripSeparator());
            _menu.Items.Add(exit);

            var icon = new WinForms.NotifyIcon
            {
                Icon = _image,
                // NotifyIcon.Text 最长 127 个字符。
                Text = tooltip.Length > 120 ? tooltip[..120] : tooltip,
                ContextMenuStrip = _menu,
            };
            icon.MouseDoubleClick += (_, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left)
                {
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                }
            };
            icon.Visible = true;
            _icon = icon;
            return true;
        }
        catch (Exception)
        {
            ReleaseResources();
            return false;
        }
    }

    public void ShowBalloon(string title, string text) => ShowBalloon(title, text, warning: false);

    /// <summary>
    /// 显示一条气泡通知。返回 true 只表示已提交给通知区域图标；Windows 的专注助手、通知开关等可能仍不显示，
    /// 不能据此宣称用户已看到。失败从不抛出。
    /// </summary>
    public bool ShowBalloon(string title, string text, bool warning)
    {
        if (!IsReady)
        {
            return false;
        }

        try
        {
            _icon!.ShowBalloonTip(8000, title, text, warning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    bool IProblemToastSink.Show(string title, string text, bool warning) => ShowBalloon(title, text, warning);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseResources();
    }

    private void ReleaseResources()
    {
        try
        {
            if (_icon is not null)
            {
                // 先隐藏再释放，避免残留“幽灵”图标。
                _icon.Visible = false;
                _icon.Dispose();
            }
        }
        catch (Exception)
        {
        }

        _icon = null;
        try { _menu?.Dispose(); } catch (Exception) { }
        _menu = null;
        try { _image?.Dispose(); } catch (Exception) { }
        _image = null;
    }

    /// <summary>
    /// 托盘图标：只加载嵌入的同一份多尺寸 ICO（Assets/ProbeDesktop.ico），按系统小图标尺寸选帧。
    /// 资源缺失或格式损坏时抛出异常，由 TryInitialize 捕获并返回 false（窗口则保持普通最小化到任务栏）。
    /// </summary>
    private static Drawing.Icon CreateIcon()
    {
        var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/ProbeDesktop.ico"))
            ?? throw new InvalidOperationException("托盘图标资源缺失。");
        using var stream = info.Stream;
        return new Drawing.Icon(stream, WinForms.SystemInformation.SmallIconSize);
    }
}
