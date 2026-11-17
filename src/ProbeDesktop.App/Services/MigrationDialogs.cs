using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace ProbeDesktop.App.Services;

/// <summary>完整数据迁移专用的对话框：迁移包选择与 PasswordBox 密码输入。与脱敏报告导出的对话框分开。</summary>
public interface IMigrationDialogs
{
    /// <summary>新建迁移包的保存位置；null 表示取消。不提示覆盖，已存在的文件由调用方拒绝。</summary>
    string? PickMigrationSavePath(string suggestedFileName);

    /// <summary>选择要导入的迁移包；null 表示取消。</summary>
    string? PickMigrationOpenPath();

    /// <summary>导出密码：两次输入完全一致、长度 12–1024 个字符，不裁剪空白。null 表示取消。</summary>
    string? AskExportPassword();

    /// <summary>导入密码：单次输入，长度 12–1024 个字符，不裁剪空白。null 表示取消。</summary>
    string? AskImportPassword();
}

public sealed class MigrationDialogs : IMigrationDialogs
{
    public const int MinPasswordLength = 12;
    public const int MaxPasswordLength = 1024;
    public const string FileExtension = ".pdmig";
    private const string Filter = "ProbeDesktop 迁移包 (*.pdmig)|*.pdmig|所有文件 (*.*)|*.*";

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

    /// <summary>默认本地目录：优先“下载”文件夹，其次用户目录；不默认“文档”，避免默认落入可能被云同步的位置。</summary>
    internal static string DefaultDirectory()
    {
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile))
            {
                var downloads = Path.Combine(profile, "Downloads");
                return Directory.Exists(downloads) ? downloads : profile;
            }
        }
        catch (Exception)
        {
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    public string? PickMigrationSavePath(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存完整数据迁移包（含 API Key，密码加密）",
            Filter = Filter,
            DefaultExt = FileExtension,
            AddExtension = true,
            FileName = suggestedFileName,
            // Core 以不覆盖方式新建文件；已存在的文件由调用方明确拒绝，而不是询问“覆盖”。
            OverwritePrompt = false,
            CheckPathExists = true,
            InitialDirectory = DefaultDirectory(),
        };

        var ok = Owner is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return ok == true ? Path.GetFullPath(dialog.FileName) : null;
    }

    public string? PickMigrationOpenPath()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要导入的完整数据迁移包",
            Filter = Filter,
            DefaultExt = FileExtension,
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            InitialDirectory = DefaultDirectory(),
        };

        var ok = Owner is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return ok == true ? Path.GetFullPath(dialog.FileName) : null;
    }

    public string? AskExportPassword() => AskPassword(
        "设置迁移包密码",
        "请设置迁移包密码（至少 12 个字符）。导入时必须输入完全相同的密码；密码丢失后迁移包无法恢复，程序不会保存或找回密码。首尾空格也算作密码的一部分。",
        confirm: true);

    public string? AskImportPassword() => AskPassword(
        "输入迁移包密码",
        "请输入导出迁移包时设置的密码。密码只用于本次验证解密，不会保存。首尾空格也算作密码的一部分。",
        confirm: false);

    /// <summary>纯函数校验，便于审查：返回 null 表示通过，否则为提示文字。不裁剪空白。</summary>
    internal static string? Validate(string first, string? second, bool confirm)
    {
        if (first.Length < MinPasswordLength)
        {
            return $"密码至少需要 {MinPasswordLength} 个字符（当前 {first.Length} 个）。";
        }

        if (first.Length > MaxPasswordLength)
        {
            return $"密码不能超过 {MaxPasswordLength} 个字符。";
        }

        if (confirm && !string.Equals(first, second, StringComparison.Ordinal))
        {
            return "两次输入的密码不一致。";
        }

        return null;
    }

    private static string? AskPassword(string title, string prompt, bool confirm)
    {
        var owner = Owner;
        var work = SystemParameters.WorkArea;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });

        var firstLabel = new Label { Content = "密码(_P)", Padding = new Thickness(0, 0, 0, 4) };
        var first = new PasswordBox { MaxLength = MaxPasswordLength, Padding = new Thickness(6, 4, 6, 4) };
        firstLabel.Target = first;
        System.Windows.Automation.AutomationProperties.SetName(first, "迁移包密码");
        panel.Children.Add(firstLabel);
        panel.Children.Add(first);

        PasswordBox? second = null;
        if (confirm)
        {
            var secondLabel = new Label { Content = "再次输入密码(_R)", Padding = new Thickness(0, 10, 0, 4) };
            second = new PasswordBox { MaxLength = MaxPasswordLength, Padding = new Thickness(6, 4, 6, 4) };
            secondLabel.Target = second;
            System.Windows.Automation.AutomationProperties.SetName(second, "再次输入迁移包密码");
            panel.Children.Add(secondLabel);
            panel.Children.Add(second);
        }

        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Application.Current?.TryFindResource("DangerBrush") as Brush ?? Brushes.DarkRed,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        System.Windows.Automation.AutomationProperties.SetLiveSetting(error, System.Windows.Automation.AutomationLiveSetting.Assertive);
        panel.Children.Add(error);

        var ok = new Button { Content = "确定", MinWidth = 96, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "取消", MinWidth = 96, Padding = new Thickness(12, 4, 12, 4), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = title,
            Content = panel,
            Width = Math.Min(480, work.Width),
            SizeToContent = SizeToContent.Height,
            MaxHeight = work.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner is null,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Background = SystemColors.ControlBrush,
        };
        if (owner is not null)
        {
            window.Owner = owner;
        }

        string? result = null;
        ok.Click += (_, _) =>
        {
            // 直接读取原值，不 Trim；校验失败时清空两个输入框，避免把错误输入留在控件里。
            var a = first.Password;
            var b = second?.Password;
            var problem = Validate(a, b, confirm);
            if (problem is not null)
            {
                error.Text = problem;
                error.Visibility = Visibility.Visible;
                if (confirm && a.Length >= MinPasswordLength && a.Length <= MaxPasswordLength)
                {
                    second!.Clear();
                    second.Focus();
                }
                else
                {
                    first.Clear();
                    second?.Clear();
                    first.Focus();
                }

                return;
            }

            result = a;
            window.Close();
        };

        window.Loaded += (_, _) => first.Focus();
        window.Closed += (_, _) =>
        {
            // 控件中的密码在对话框关闭时清空；托管字符串本身无法保证被擦除。
            first.Clear();
            second?.Clear();
        };

        window.ShowDialog();
        return result;
    }
}
