using System.Windows;
using System.Windows.Threading;
using ProbeDesktop.App.Services;
using ProbeDesktop.App.ViewModels;
using ProbeDesktop.Core;

namespace ProbeDesktop.App;

public partial class App : Application
{
    private SingleInstanceGuard? _guard;
    private IDesktopService? _service;
    private MainViewModel? _main;
    private TrayService? _tray;
    private int _fatalShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        var dialogs = new DialogService();
        UiErrors.Configure(dialogs);

        if (!StartupParser.TryParse(e.Args, out var options, out var error) || options is null)
        {
            dialogs.Error("启动参数错误", error);
            Shutdown(2);
            return;
        }

        try
        {
            _guard = SingleInstanceGuard.TryAcquire(options.DataDirectory);
        }
        catch (Exception)
        {
            _guard = null;
        }

        if (_guard is null)
        {
            dialogs.Info("程序已在运行",
                "使用同一数据目录的程序已经在运行，请切换到已打开的窗口。若它已最小化到通知区域，请到任务栏右侧的通知区域（可能在“显示隐藏的图标”溢出菜单中）双击图标，或右键选择“打开主窗口”。本次启动不会唤醒已运行的窗口。\n\n为避免两个窗口同时修改本地数据或重复提交检测，同一数据目录只允许运行一个实例。\n\n数据目录：" + options.DataDirectory);
            Shutdown(1);
            return;
        }

        try
        {
            // Core 负责创建目录、校验重解析点并收紧权限。
            _service = new DesktopService(options.DataDirectory);
        }
        catch (Exception ex)
        {
            dialogs.Error("无法初始化本地数据",
                "无法打开数据目录：\n" + options.DataDirectory + "\n\n" + UserMessages.Describe(ex));
            Cleanup();
            Shutdown(3);
            return;
        }

        try
        {
            _tray = new TrayService();
            // 托盘同时作为问题通知的出口（复用现有托盘气泡）。
            _main = new MainViewModel(_service, dialogs, options.IsCustomDataDirectory, sink: _tray);
            var window = new MainWindow(_main, dialogs, _tray);
            MainWindow = window;
            window.Closed += (_, _) => Shutdown(0);
            window.Show();
        }
        catch (Exception ex)
        {
            dialogs.Error("无法打开主窗口", UserMessages.Describe(ex));
            Cleanup();
            Shutdown(4);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        base.OnExit(e);
    }

    private void Cleanup()
    {
        try
        {
            _tray?.Dispose();
        }
        catch
        {
        }

        _tray = null;
        try
        {
            _main?.Dispose();
        }
        catch
        {
        }

        _main = null;
        try
        {
            _service?.Dispose();
        }
        catch
        {
        }

        _service = null;
        _guard?.Dispose();
        _guard = null;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 不显示异常原文与堆栈，避免泄漏敏感信息。
        e.Handled = true;
        if (Interlocked.Exchange(ref _fatalShown, 1) == 1)
        {
            return;
        }

        try
        {
            MessageBox.Show(
                "程序遇到未预期的错误：" + UserMessages.Describe(e.Exception) + "\n\n已阻止该操作继续。若界面状态异常，建议关闭后重新打开程序；本地数据不会因此被清空。",
                "程序错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
        }
        finally
        {
            Interlocked.Exchange(ref _fatalShown, 0);
        }
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            Cleanup();
        }
        catch
        {
        }
    }
}
