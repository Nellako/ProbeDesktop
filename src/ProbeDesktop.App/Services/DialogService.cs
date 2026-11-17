using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace ProbeDesktop.App.Services;

public interface IDialogService
{
    /// <summary>是/否确认，默认按钮为“否”。仅返回 Yes 时为 true。</summary>
    bool Confirm(string title, string message, bool warning = true);
    void Info(string title, string message);
    void Error(string title, string message);
    void ShowException(string title, Exception ex);
    /// <summary>返回 null 表示用户取消。</summary>
    string? PickExportPath(string suggestedFileName);
}

public sealed class DialogService : IDialogService
{
    private static Window? Owner
    {
        get
        {
            var app = Application.Current;
            if (app is null)
            {
                return null;
            }

            var active = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
            var owner = active ?? app.MainWindow;
            return owner is { IsVisible: true } ? owner : null;
        }
    }

    /// <summary>超过任一阈值就改用可滚动确认窗，避免 MessageBox 在内容很多时超出屏幕而无法看到完整接收方/模型范围。</summary>
    private const int LongTextChars = 1200;
    private const int LongTextLines = 18;

    internal static bool IsLongText(string message) =>
        message.Length > LongTextChars || message.Count(c => c == '\n') + 1 > LongTextLines;

    public bool Confirm(string title, string message, bool warning = true)
    {
        var owner = Owner;
        if (IsLongText(message))
        {
            return ConfirmScrollable(owner, title, message);
        }

        var icon = warning ? MessageBoxImage.Warning : MessageBoxImage.Question;
        var result = owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, icon, MessageBoxResult.No)
            : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, icon, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    /// <summary>
    /// 长文本确认窗：全文放在 ScrollViewer 内自动换行，不截断。默认且取消键均为“取消”，只有点“确认发送”才返回 true；
    /// 关闭窗口、Esc、Enter 默认都等于取消。窗口最大不超过工作区，内容多时出现滚动条。
    /// </summary>
    private static bool ConfirmScrollable(Window? owner, string title, string message)
    {
        var work = SystemParameters.WorkArea;
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12),
        };
        var scroll = new ScrollViewer
        {
            Content = text,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = true,
        };
        var border = new Border
        {
            Child = scroll,
            Background = SystemColors.WindowBrush,
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(12, 12, 12, 0),
        };

        var confirm = new Button { Content = "确认发送", MinWidth = 96, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", MinWidth = 96, Padding = new Thickness(12, 4, 12, 4), IsCancel = true, IsDefault = true };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12),
        };
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(border, 0);
        Grid.SetRow(buttons, 1);
        grid.Children.Add(border);
        grid.Children.Add(buttons);

        var window = new Window
        {
            Title = title,
            Content = grid,
            Width = Math.Min(760, work.Width),
            MinWidth = Math.Min(420, work.Width),
            MaxWidth = work.Width,
            MaxHeight = work.Height,
            SizeToContent = SizeToContent.Height,
            ShowInTaskbar = owner is null,
            ResizeMode = ResizeMode.CanResizeWithGrip,
            Background = SystemColors.ControlBrush,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        if (owner is not null)
        {
            window.Owner = owner;
        }

        var confirmed = false;
        confirm.Click += (_, _) =>
        {
            confirmed = true;
            window.Close();
        };

        // 焦点保持在“取消”上，方向键/翻页键直接滚动全文，避免误按 Enter 确认。
        window.PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Up: scroll.LineUp(); e.Handled = true; break;
                case Key.Down: scroll.LineDown(); e.Handled = true; break;
                case Key.PageUp: scroll.PageUp(); e.Handled = true; break;
                case Key.PageDown: scroll.PageDown(); e.Handled = true; break;
                case Key.Home: scroll.ScrollToTop(); e.Handled = true; break;
                case Key.End: scroll.ScrollToBottom(); e.Handled = true; break;
            }
        };
        window.Loaded += (_, _) =>
        {
            // Window.MaxHeight 含标题栏；若内容仍比可视区高，ScrollViewer 会出现滚动条。同时保证窗口在工作区内。
            if (window.Top < work.Top)
            {
                window.Top = work.Top;
            }

            if (window.Top + window.ActualHeight > work.Bottom)
            {
                window.Top = Math.Max(work.Top, work.Bottom - window.ActualHeight);
            }

            cancel.Focus();
        };

        window.ShowDialog();
        return confirmed;
    }

    public void Info(string title, string message) => Show(title, message, MessageBoxImage.Information);

    public void Error(string title, string message) => Show(title, message, MessageBoxImage.Error);

    public void ShowException(string title, Exception ex) => Error(title, UserMessages.Describe(ex));

    public string? PickExportPath(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出检测记录（JSON，不含 API Key）",
            Filter = "JSON 文件 (*.json)|*.json",
            DefaultExt = ".json",
            AddExtension = true,
            FileName = suggestedFileName,
            // Core 以新建方式写入，已存在的文件由我们给出明确提示，而不是“覆盖”确认。
            OverwritePrompt = false,
            CheckPathExists = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };

        var ok = Owner is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return ok == true ? Path.GetFullPath(dialog.FileName) : null;
    }

    private static void Show(string title, string message, MessageBoxImage icon)
    {
        var owner = Owner;
        if (owner is null)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, icon);
        }
        else
        {
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, icon);
        }
    }
}

/// <summary>命令层的兜底错误报告。</summary>
public static class UiErrors
{
    private static IDialogService _dialogs = new DialogService();

    public static void Configure(IDialogService dialogs) => _dialogs = dialogs;

    public static void Report(Exception ex)
    {
        try
        {
            _dialogs.ShowException("操作失败", ex);
        }
        catch
        {
            // 无法显示对话框时不再抛出。
        }
    }
}
