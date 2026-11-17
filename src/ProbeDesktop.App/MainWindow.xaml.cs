using System.ComponentModel;
using System.Windows;
using ProbeDesktop.App.Services;
using ProbeDesktop.App.ViewModels;
using ProbeDesktop.App.Windows;

namespace ProbeDesktop.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly IDialogService _dialogs;
    private readonly TrayService? _tray;
    private WindowState _restoreState = WindowState.Normal;
    private bool _trayInitialized;
    private bool _balloonShown;
    private bool _restoring;
    private bool _closeCancelled;
    private bool _exitingFromTray;
    private Window? _connectionWindow;
    private Window? _probeWindow;

    public MainWindow(MainViewModel vm, IDialogService dialogs, TrayService? tray = null)
    {
        _vm = vm;
        _dialogs = dialogs;
        _tray = tray;
        DataContext = vm;
        InitializeComponent();
        vm.OpenWindowRequested = OpenRecordWindow;
        vm.Migration.ExitRequested = () => Close();
        vm.Migration.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MigrationViewModel.IsLocked))
            {
                SyncChildLock();
            }
        };
        vm.RestartRequired += (_, _) => CloseChildren();
        Loaded += OnLoadedOnce;
    }

    /// <summary>迁移忙碌时记录窗口的内容也禁用（窗口本身仍可移动/关闭），避免主窗禁用而子窗仍可操作。</summary>
    private void SyncChildLock()
    {
        var locked = _vm.Migration.IsLocked;
        foreach (var child in new[] { _connectionWindow, _probeWindow })
        {
            if (child?.Content is UIElement content)
            {
                content.IsEnabled = !locked;
            }
        }
    }

    /// <summary>导入提交后关闭两个记录窗口：它们持有旧内存状态，不应再操作。</summary>
    private void CloseChildren()
    {
        _connectionWindow?.Close();
        _probeWindow?.Close();
    }

    private bool _loadedHandled;

    /// <summary>Loaded 只处理一次：主窗口已显示后再异步创建托盘图标，失败不影响窗口可见。</summary>
    private async void OnLoadedOnce(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOnce;
        if (_loadedHandled)
        {
            return;
        }

        _loadedHandled = true;
        _ = Dispatcher.BeginInvoke(new Action(InitializeTrayOnce), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        try
        {
            await _vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            UiErrors.Report(ex);
        }
    }

    /// <summary>托盘图标和事件订阅只创建一次。失败时 IsReady 为 false，窗口保持普通任务栏行为。</summary>
    private void InitializeTrayOnce()
    {
        if (_trayInitialized || _tray is null)
        {
            return;
        }

        _trayInitialized = true;
        _tray.OpenRequested += (_, _) => RestoreFromTray();
        _tray.ExitRequested += (_, _) => ExitFromTray();
        _tray.ShowConnectionsRequested += (_, _) => OpenRecordWindow("connections");
        _tray.ShowProbeHistoryRequested += (_, _) => OpenRecordWindow("probe");
        // 创建失败时 IsReady 为 false，最小化保持普通任务栏行为，不隐藏窗口。
        _tray.TryInitialize("中转模型检测（非官方）：仍在运行，双击打开");
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (_restoring)
        {
            return;
        }

        if (WindowState != WindowState.Minimized)
        {
            _restoreState = WindowState;
            return;
        }

        // 只有图标已成功创建才隐藏，否则保持在任务栏，避免窗口“消失”。
        if (_tray is not { IsReady: true })
        {
            return;
        }

        ShowInTaskbar = false;
        Hide();
        if (!_balloonShown)
        {
            _balloonShown = true;
            var auto = _vm.Inspection.Enabled || _vm.IdentityInspection.Enabled;
            _tray.ShowBalloon(
                "程序仍在运行",
                "已最小化到通知区域（图标可能在“显示隐藏的图标”溢出菜单中）。最小化不等于退出。"
                + (auto ? "连接巡检或身份识别巡检仍在按间隔运行，可能产生费用（身份识别会向 BazaarLink 提交）；如需停止，请打开窗口关闭对应开关，或退出程序。" : "双击图标可恢复窗口。"));
        }
    }

    private void RestoreFromTray()
    {
        if (_restoring)
        {
            return;
        }

        _restoring = true;
        try
        {
            ShowInTaskbar = true;
            if (!IsVisible)
            {
                Show();
            }

            WindowState = _restoreState == WindowState.Minimized ? WindowState.Normal : _restoreState;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>托盘“退出”：复用右上角 X 的 Close 流程；用户取消时图标保留并让窗口重新可见。</summary>
    private void ExitFromTray()
    {
        if (_exitingFromTray)
        {
            return;
        }

        _exitingFromTray = true;
        try
        {
            _closeCancelled = false;
            if (!IsVisible)
            {
                // 先显示窗口，确认对话框才有可见的所有者。
                RestoreFromTray();
            }

            Close();
            if (_closeCancelled)
            {
                RestoreFromTray();
            }
        }
        finally
        {
            _exitingFromTray = false;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 迁移进行中：可取消阶段先取消，写入阶段不可中断；两种情况都不在此刻关闭。
        if (_vm.Migration.IsBusy)
        {
            var cancellable = _vm.Migration.CanCancel;
            _vm.Migration.TryCancelForClose();
            _dialogs.Info("迁移进行中",
                cancellable
                    ? "已请求取消本次迁移。取消完成后可以再关闭程序。"
                    : "正在进行迁移，写入本机数据的阶段不能中断，或仍在等待你的确认。请先完成或取消当前步骤，再关闭程序。");
            _closeCancelled = true;
            e.Cancel = true;
            return;
        }

        var warning = _vm.ExitWarning();
        if (warning is null && (_vm.Inspection.Enabled || _vm.Inspection.IsBusy))
        {
            warning = _vm.Inspection.IsBusy
                ? "有一轮连接巡检正在进行。现在退出会取消本轮，尚未发送的重试和其余目标都不会再发送；已发出的请求无法撤回，中转站侧可能已经产生费用（含本轮已重试的请求），退出后也不会自动补发。\n\n确定要退出吗？"
                : "全局连接巡检仍处于开启状态。退出会停止本机的定时巡检，之后不再发送；已发出的请求无法撤回，中转站侧可能已经产生费用。下次启动会在已授权范围内自动恢复。\n\n确定要退出吗？";
        }

        // 身份识别巡检的提示与其他提示合并为同一个确认框,避免连续弹两次。
        var identity = _vm.IdentityInspectionExitWarning();
        if (identity is not null)
        {
            const string Ask = "\n\n确定要退出吗？";
            warning = warning is null
                ? identity + Ask
                : (warning.EndsWith(Ask, StringComparison.Ordinal) ? warning[..^Ask.Length] : warning) + "\n\n另外：" + identity + Ask;
        }

        if (warning is not null && !_dialogs.Confirm("确认关闭", warning))
        {
            _closeCancelled = true;
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>打开连接记录或鉴别记录窗口。每类只保留一个实例，独立于主窗口的隐藏/最小化。</summary>
    private void OpenRecordWindow(string which)
    {
        // 迁移进行中或等待重启：不打开记录窗口（含托盘菜单入口）。
        if (_vm.Migration.IsLocked)
        {
            if (_vm.Migration.IsRestartRequired)
            {
                RestoreFromTray();
            }

            return;
        }

        if (which == "connections")
        {
            _connectionWindow ??= CreateChild(new ConnectionHistoryWindow(_vm.ConnectionHistory), () => _connectionWindow = null);
            ShowChild(_connectionWindow);
        }
        else
        {
            _probeWindow ??= CreateChild(new ProbeHistoryWindow(_vm.History), () => _probeWindow = null);
            ShowChild(_probeWindow);
        }
    }

    private static Window CreateChild(Window window, Action onClosed)
    {
        window.Closed += (_, _) => onClosed();
        return window;
    }

    private static void ShowChild(Window window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    protected override void OnClosed(EventArgs e)
    {
        // 主窗口真正退出时关闭两个记录窗口；它们不随主窗口隐藏而隐藏。
        _connectionWindow?.Close();
        _probeWindow?.Close();

        // 真正关闭后才释放托盘图标和菜单（幂等）；App.Cleanup 另有兜底。
        _tray?.Dispose();
        base.OnClosed(e);
    }
}
